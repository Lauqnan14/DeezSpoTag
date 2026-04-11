using System.Text.Json;
using System.Linq;
using System.Buffers;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download;
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
        SongLinkResult? Availability,
        bool UseAtmosStereoDual);

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
        string Engine,
        string? ContentType,
        int? DurationMs,
        string TrackTitle,
        string TrackArtist,
        string? TrackPrimaryArtist,
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

    private sealed record PrimaryPayloadEnqueueContext(
        DownloadIntent Intent,
        DeezSpoTagSettings Settings,
        long? SecondaryDestinationFolderId,
        bool AllowQualityUpgrade,
        int? RequestedQualityRank,
        bool UseAtmosStereoDual,
        List<string> Queued,
        List<string> SkipReasonCodes,
        List<string> SkipReasons,
        SongLinkResult? Availability,
        bool PreferIsrcOnly,
        CancellationToken CancellationToken);

    private sealed record AppleSecondaryEnqueueRequest(
        DownloadIntent Intent,
        DeezSpoTagSettings Settings,
        long? SecondaryDestinationFolderId,
        bool AllowQualityUpgrade,
        List<string> Queued,
        SongLinkResult? Availability,
        bool PreferIsrcOnly,
        CancellationToken CancellationToken);

    private sealed record LibraryDuplicateCheck(
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
        string TrackTitle,
        long? DestinationFolderId,
        string? RequestedAudioVariant,
        CancellationToken CancellationToken);

    private sealed record EngineEnqueueRequest(
        DownloadIntent Intent,
        DeezSpoTagSettings Settings,
        string Engine,
        string? SelectedQuality,
        string? ResolvedSourceUrl,
        bool UseAtmosStereoDual,
        long? PrimaryDestinationFolderId,
        long? SecondaryDestinationFolderId,
        int DurationSeconds,
        int? RequestedQualityRank,
        SongLinkResult? Availability,
        bool PreferIsrcOnly,
        List<string> Queued,
        List<string> SkipReasonCodes,
        List<string> SkipReasons,
        (List<FallbackPlanStep> FallbackPlan, List<string> AutoSources, int AutoIndex) FallbackInfo,
        CancellationToken CancellationToken);

    private sealed record EnqueueItemContext(
        PayloadIdentity Identity,
        DeezSpoTagSettings Settings,
        bool AllowQualityUpgrade,
        int? RequestedQualityRank,
        bool QueueQualityUpgradeRequested,
        int RequestedRank,
        int? RequestedLocalQualityRank,
        bool LocalQualityUpgradeRequested);

    private sealed record QueueDuplicateResolution(
        EnqueueItemDecision? Decision,
        bool AllowInsert);

    private sealed record EngineEnqueueOutcome(
        DownloadIntentResult? Failure,
        int Skipped);

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
        List<string> AutoSources);

    private sealed record IntentResolutionBootstrap(
        string SourceUrl,
        bool IsPodcastIntent,
        string? NormalizedDeezerId);

    private const string SpotifyPlatform = "spotify";
    private const string AutoService = "auto";
    private const string AtmosQuality = "atmos";
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
    private const string SonglinkSpotifyKey = "songlink-spotify";
    private const string AppleMusicDomain = "music.apple.com";
    private const string DeezerDomain = "deezer.com";
    private const string QobuzDomain = "qobuz.com";
    private const string AttributesField = "attributes";
    private static readonly HashSet<string> BlockedGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "other",
        "others"
    };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly SearchValues<char> QueryFragmentSeparators = SearchValues.Create("?#");
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly IActivityLogWriter _activityLog;
    private readonly DeezerClient _deezerClient;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly TidalDownloadService _tidalDownloadService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly SpotifyPathfinderMetadataClient _spotifyPathfinderClient;
    private readonly ArtistPageCacheRepository _artistPageCacheRepository;
    private readonly IDownloadTagSettingsResolver _downloadTagSettingsResolver;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly ILogger<DownloadIntentService> _logger;
    private IReadOnlyDictionary<string, string>? _genreAliasMap;
    private bool _genreTagNormalizationEnabled;

    public DownloadIntentService(
        ILogger<DownloadIntentService> logger,
        IServiceProvider serviceProvider)
    {
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _orchestrationService = serviceProvider.GetRequiredService<DownloadOrchestrationService>();
        _deezspotagListener = serviceProvider.GetRequiredService<IDeezSpoTagListener>();
        _songLinkResolver = serviceProvider.GetRequiredService<SongLinkResolver>();
        _spotifyIdResolver = serviceProvider.GetRequiredService<ISpotifyIdResolver>();
        _activityLog = serviceProvider.GetRequiredService<IActivityLogWriter>();
        _deezerClient = serviceProvider.GetRequiredService<DeezerClient>();
        _authenticatedDeezerService = serviceProvider.GetRequiredService<AuthenticatedDeezerService>();
        _tidalDownloadService = serviceProvider.GetRequiredService<TidalDownloadService>();
        _appleCatalogService = serviceProvider.GetRequiredService<AppleMusicCatalogService>();
        _libraryRepository = serviceProvider.GetRequiredService<LibraryRepository>();
        _spotifyMetadataService = serviceProvider.GetRequiredService<SpotifyMetadataService>();
        _spotifyPathfinderClient = serviceProvider.GetRequiredService<SpotifyPathfinderMetadataClient>();
        _artistPageCacheRepository = serviceProvider.GetRequiredService<ArtistPageCacheRepository>();
        _downloadTagSettingsResolver = serviceProvider.GetRequiredService<IDownloadTagSettingsResolver>();
        _boomplayMetadataService = serviceProvider.GetRequiredService<BoomplayMetadataService>();
        _logger = logger;
    }

    public async Task<DownloadIntentResult> EnqueueAsync(
        DownloadIntent intent,
        CancellationToken cancellationToken,
        bool preferIsrcOnly = false)
    {
        var resolution = await TryPrepareEnqueueResolutionAsync(intent, preferIsrcOnly, cancellationToken);
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
        LogResolvedIntentMapping(resolvedTarget.Resolution, engine);
        var autoSources = resolvedTarget.AutoSources;
        var availability = routing.Availability;
        var useAtmosStereoDual = routing.UseAtmosStereoDual;
        var allowCrossEngineFallback = resolvedTarget.AllowCrossEngineFallback;
        var availabilityWarning = resolvedTarget.AvailabilityWarning;
        var resolved = resolvedTarget.Resolution;
        var resolvedSourceUrl = resolved.SourceUrl;
        var isMusicIntent = IsMusicIntent(intent);
        var durationSeconds = intent.DurationMs > 0 ? (int)Math.Round(intent.DurationMs / 1000d) : 0;
        var queued = new List<string>();
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
            autoSources));
        var fallbackPlan = primaryFallback.FallbackPlan;
        var selectedAutoIndex = primaryFallback.AutoIndex;

        if (fallbackPlan.Count > 0)
        {
            var planSummary = string.Join(" → ", fallbackPlan.Select(step =>
                string.IsNullOrWhiteSpace(step.Quality) ? step.Engine : $"{step.Engine}|{step.Quality}"));
            _activityLog.Info($"Fallback plan: start_index={selectedAutoIndex} steps=[{planSummary}]");
        }
        var requestedQualityRank = ParseRequestedQualityRank(selectedQuality ?? intent.Quality);
        var primaryDestinationFolderId = useMultiQuality
            ? (multiQuality!.PrimaryDestinationFolderId ?? intent.DestinationFolderId)
            : intent.DestinationFolderId;
        var secondaryDestinationFolderId = useMultiQuality
            ? (intent.SecondaryDestinationFolderId ?? multiQuality!.SecondaryDestinationFolderId)
            : null;

        intent.AlbumArtist = ResolveEffectiveAlbumArtist(
            intent.AlbumArtist,
            intent.Artist,
            settings.Tags?.SingleAlbumArtist != false);
        var request = new EngineEnqueueRequest(
            intent,
            settings,
            engine,
            selectedQuality,
            resolvedSourceUrl,
            useAtmosStereoDual,
            primaryDestinationFolderId,
            secondaryDestinationFolderId,
            durationSeconds,
            requestedQualityRank,
            availability,
            preferIsrcOnly,
            queued,
            skipReasonCodes,
            skipReasons,
            primaryFallback,
            cancellationToken);
        var enqueueOutcome = await EnqueueByEngineAsync(request);
        if (enqueueOutcome.Failure != null)
        {
            return enqueueOutcome.Failure;
        }

        var skipped = enqueueOutcome.Skipped;

        if (queued.Count > 0)
        {
            _activityLog.Info($"Intent queued: engine={engine} count={queued.Count}");
            if (IsMusicIntent(intent))
            {
                _orchestrationService.MarkDownloadQueued();
            }
        }

        var message = queued.Count > 0 ? $"Queued {queued.Count} item(s)." : (skipReasons.FirstOrDefault() ?? "Nothing queued.");
        if (!string.IsNullOrWhiteSpace(availabilityWarning))
        {
            message = $"{message} {availabilityWarning}";
        }

        return new DownloadIntentResult
        {
            Success = queued.Count > 0,
            Engine = engine,
            Queued = queued,
            Skipped = skipped,
            Message = message,
            SkipReasonCodes = skipReasonCodes,
            SkipReasons = skipReasons
        };
    }

    private async Task<(DownloadIntentResult? Failure, EnqueueResolutionState? State)> TryPrepareEnqueueResolutionAsync(
        DownloadIntent intent,
        bool preferIsrcOnly,
        CancellationToken cancellationToken)
    {
        var gateFailure = await TryBlockByDownloadGateAsync(cancellationToken);
        if (gateFailure != null)
        {
            return (gateFailure, null);
        }

        var preparation = await PrepareEnqueueAsync(intent, cancellationToken);
        var profileFailure = await TryValidateEnqueueProfileAsync(intent, preparation, cancellationToken);
        if (profileFailure != null)
        {
            return (profileFailure, null);
        }

        await PopulateIntentMetadataAsync(intent, preparation.Settings, cancellationToken);
        var blocklistFailure = await TryBlockByGlobalBlocklistAsync(intent, cancellationToken);
        if (blocklistFailure != null)
        {
            return (blocklistFailure, null);
        }

        var settings = preparation.Settings;
        var routingFailure = TryValidateExplicitEngineRouting(intent, preparation);
        if (routingFailure != null)
        {
            return (routingFailure, null);
        }

        var routing = await PrepareEnqueueRoutingAsync(intent, preparation, cancellationToken);
        var noSourcesFailure = TryValidateRoutingSources(routing);
        if (noSourcesFailure != null)
        {
            return (noSourcesFailure, null);
        }

        var resolvedTarget = await ResolvePrimaryEnqueueTargetAsync(intent, routing, settings, preferIsrcOnly, cancellationToken);
        if (resolvedTarget?.Resolution.Engine == string.Empty
            && !string.IsNullOrWhiteSpace(resolvedTarget?.Resolution.Message))
        {
            return (new DownloadIntentResult
            {
                Success = false,
                Message = resolvedTarget.Resolution.Message,
                Engine = resolvedTarget.Engine
            }, null);
        }
        if (resolvedTarget == null)
        {
            _activityLog.Warn("Auto mapping failed: engine=auto reason=unresolved");
            return (new DownloadIntentResult
            {
                Success = false,
                Message = "Unable to resolve mapping for any auto source.",
                Engine = string.Empty
            }, null);
        }

        return (null, new EnqueueResolutionState(settings, routing, resolvedTarget));
    }

    private static (List<FallbackPlanStep> FallbackPlan, List<string> AutoSources, int AutoIndex) BuildEnqueueFallbackInfo(
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

        var payloadSources = allowCrossEngineFallback
            ? DownloadSourceOrder.CollapseAutoSourcesByService(
                BuildFallbackPlanSources(autoSources, settings, engine, quality))
            : DownloadSourceOrder.ResolveEngineQualitySources(
                engine,
                quality,
                strict: UseStrictQualityFallback(settings, engine, quality));

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

    private static PrimaryPayloadEnqueueContext CreatePrimaryEnqueueContext(EngineEnqueueRequest request)
        => new(
            request.Intent,
            request.Settings,
            request.SecondaryDestinationFolderId,
            request.Intent.AllowQualityUpgrade,
            request.RequestedQualityRank,
            request.UseAtmosStereoDual,
            request.Queued,
            request.SkipReasonCodes,
            request.SkipReasons,
            request.Availability,
            request.PreferIsrcOnly,
            request.CancellationToken);

    private async Task<EngineEnqueueOutcome> EnqueueByEngineAsync(EngineEnqueueRequest request)
    {
        return request.Engine switch
        {
            DeezerPlatform => await EnqueueDeezerAsync(request),
            TidalPlatform => await EnqueueTidalAsync(request),
            AmazonPlatform => await EnqueueAmazonAsync(request),
            ApplePlatform => await EnqueueAppleAsync(request),
            QobuzPlatform => await EnqueueQobuzAsync(request),
            _ => new EngineEnqueueOutcome(
                new DownloadIntentResult
                {
                    Success = false,
                    Message = $"Unsupported engine: {request.Engine}",
                    Engine = request.Engine
                },
                0)
        };
    }

    private async Task<EngineEnqueueOutcome> EnqueueDeezerAsync(EngineEnqueueRequest request)
    {
        if (!_deezerClient.LoggedIn)
        {
            return new EngineEnqueueOutcome(
                new DownloadIntentResult { Success = false, Message = "Deezer login required.", Engine = request.Engine },
                0);
        }

        var resolvedSource = request.ResolvedSourceUrl ?? request.Intent.SourceUrl;
        var isPodcastIntent = string.Equals(
            NormalizeContentType(request.Intent.ContentType),
            DownloadContentTypes.Podcast,
            StringComparison.OrdinalIgnoreCase)
            || IsPodcastSource(resolvedSource, null);
        var deezerTrackId = ResolveDeezerTrackIdForEnqueue(request.Intent, request.ResolvedSourceUrl, isPodcastIntent);
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            var idType = isPodcastIntent ? EpisodeType : TrackType;
            return new EngineEnqueueOutcome(
                new DownloadIntentResult
                {
                    Success = false,
                    Message = $"Deezer {idType} ID unavailable for this item.",
                    Engine = request.Engine
                },
                0);
        }

        var collectionType = EpisodeType;
        if (!isPodcastIntent)
        {
            collectionType = string.IsNullOrWhiteSpace(request.Intent.Album) ? TrackType : AlbumType;
        }
        var requestedBitrate = int.TryParse(request.SelectedQuality, out var parsedBitrate) ? parsedBitrate : 0;
        var bitrate = isPodcastIntent
            ? 0
            : DownloadSourceOrder.ResolveDeezerBitrate(request.Settings, requestedBitrate);
        var sourceUrl = ResolveDeezerSourceUrl(request.ResolvedSourceUrl, deezerTrackId, isPodcastIntent);
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return new EngineEnqueueOutcome(
                new DownloadIntentResult { Success = false, Message = "Deezer URL unavailable for this track.", Engine = request.Engine },
                0);
        }

        var resolvedQuality = request.SelectedQuality;
        if (string.IsNullOrWhiteSpace(resolvedQuality))
        {
            resolvedQuality = isPodcastIntent ? DownloadContentTypes.Podcast : bitrate.ToString();
        }
        var payload = new DeezerQueueItem
        {
            CollectionType = collectionType,
            DeezerId = deezerTrackId,
            DeezerAlbumId = request.Intent.DeezerAlbumId ?? string.Empty,
            DeezerArtistId = request.Intent.DeezerArtistId ?? string.Empty,
            Quality = resolvedQuality,
            Bitrate = bitrate
        };
        PopulateStandardQueuePayload(payload, request.Intent, new StandardPayloadContext(
            sourceUrl,
            collectionType,
            ResolveContentType(request.Intent.ContentType, sourceUrl, collectionType, request.Intent.HasAtmos, request.SelectedQuality),
            request.FallbackInfo.AutoSources,
            request.FallbackInfo.AutoIndex,
            request.FallbackInfo.FallbackPlan,
            request.Intent.ReleaseDate ?? string.Empty,
            request.DurationSeconds,
            request.PrimaryDestinationFolderId,
            request.UseAtmosStereoDual ? StereoType : string.Empty));
        ApplyIntentMetadata(payload, request.Intent);

        var skipped = await EnqueuePrimaryPayloadAsync(payload, CreatePrimaryEnqueueContext(request));
        return new EngineEnqueueOutcome(null, skipped);
    }

    private async Task<EngineEnqueueOutcome> EnqueueTidalAsync(EngineEnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResolvedSourceUrl))
        {
            return new EngineEnqueueOutcome(
                new DownloadIntentResult { Success = false, Message = "Tidal URL unavailable for this track.", Engine = request.Engine },
                0);
        }

        var payload = new TidalQueueItem();
        PopulateStandardQueuePayload(payload, request.Intent, new StandardPayloadContext(
            request.ResolvedSourceUrl,
            string.IsNullOrWhiteSpace(request.Intent.Album) ? TrackType : AlbumType,
            ResolveContentType(request.Intent.ContentType, request.ResolvedSourceUrl, TrackType, request.Intent.HasAtmos, request.SelectedQuality),
            request.FallbackInfo.AutoSources,
            request.FallbackInfo.AutoIndex,
            request.FallbackInfo.FallbackPlan,
            string.Empty,
            request.DurationSeconds,
            request.PrimaryDestinationFolderId,
            request.UseAtmosStereoDual ? StereoType : string.Empty));
        payload.Quality = request.SelectedQuality ?? request.Settings.TidalQuality ?? "HI_RES_LOSSLESS";
        ApplyIntentMetadata(payload, request.Intent);

        var skipped = await EnqueuePrimaryPayloadAsync(payload, CreatePrimaryEnqueueContext(request));
        return new EngineEnqueueOutcome(null, skipped);
    }

    private async Task<EngineEnqueueOutcome> EnqueueAmazonAsync(EngineEnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResolvedSourceUrl))
        {
            return new EngineEnqueueOutcome(
                new DownloadIntentResult { Success = false, Message = "Amazon URL unavailable for this track.", Engine = request.Engine },
                0);
        }

        var payload = new AmazonQueueItem();
        PopulateStandardQueuePayload(payload, request.Intent, new StandardPayloadContext(
            request.ResolvedSourceUrl,
            string.IsNullOrWhiteSpace(request.Intent.Album) ? TrackType : AlbumType,
            ResolveContentType(request.Intent.ContentType, request.ResolvedSourceUrl, TrackType, request.Intent.HasAtmos, request.SelectedQuality),
            request.FallbackInfo.AutoSources,
            request.FallbackInfo.AutoIndex,
            request.FallbackInfo.FallbackPlan,
            string.Empty,
            request.DurationSeconds,
            request.PrimaryDestinationFolderId,
            request.UseAtmosStereoDual ? StereoType : string.Empty));
        payload.Quality = request.SelectedQuality ?? "FLAC";
        ApplyIntentMetadata(payload, request.Intent);

        var skipped = await EnqueuePrimaryPayloadAsync(payload, CreatePrimaryEnqueueContext(request));
        return new EngineEnqueueOutcome(null, skipped);
    }

    private async Task<EngineEnqueueOutcome> EnqueueAppleAsync(EngineEnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResolvedSourceUrl))
        {
            return new EngineEnqueueOutcome(
                new DownloadIntentResult { Success = false, Message = "Apple Music URL unavailable for this track.", Engine = request.Engine },
                0);
        }

        if (RequiresVerifiedAtmosCapability(request.Intent, request.SelectedQuality))
        {
            var hasAtmosVariant = await ValidateAppleAtmosCapabilityAsync(
                request.Intent,
                request.ResolvedSourceUrl,
                request.Availability,
                request.Settings,
                "primary",
                request.CancellationToken);
            if (!hasAtmosVariant)
            {
                request.SkipReasonCodes.Add("atmos_variant_unavailable");
                request.SkipReasons.Add("Skipped Atmos queue: no Atmos variant found for this track.");
                return new EngineEnqueueOutcome(null, 1);
            }
        }

        var selectedAutoIndex = Math.Max(0, request.FallbackInfo.AutoIndex);
        (AppleQueueItem payload, bool isVideo) = await BuildApplePayloadBaseAsync(
            request.Intent,
            request.ResolvedSourceUrl,
            request.SelectedQuality,
            request.Settings,
            request.CancellationToken);
        payload.AutoSources = request.FallbackInfo.AutoSources;
        payload.AutoIndex = selectedAutoIndex;
        payload.FallbackPlan = request.FallbackInfo.FallbackPlan;
        payload.DestinationFolderId = request.PrimaryDestinationFolderId;
        var primaryIsAtmos = string.Equals(payload.ContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase)
            || IsAtmosQuality(payload.Quality);
        payload.QualityBucket = request.UseAtmosStereoDual
            ? (primaryIsAtmos ? AtmosQuality : StereoType)
            : string.Empty;

        var enqueueDecision = await EnqueueItemAsync(
            payload,
            request.Intent.AllowQualityUpgrade,
            request.RequestedQualityRank,
            request.CancellationToken);
        if (enqueueDecision.Success)
        {
            var queueUuid = enqueueDecision.QueueUuid ?? payload.Id;
            request.Queued.Add(queueUuid);
            _deezspotagListener.SendAddedToQueue(payload.ToQueuePayload());
            if (request.UseAtmosStereoDual
                && !isVideo
                && !IsAtmosQuality(request.SelectedQuality))
            {
                await TryEnqueueAppleAtmosSecondaryAsync(
                    new AppleSecondaryEnqueueRequest(
                        request.Intent,
                        request.Settings,
                        request.SecondaryDestinationFolderId,
                        request.Intent.AllowQualityUpgrade,
                        request.Queued,
                        request.Availability,
                        request.PreferIsrcOnly,
                        request.CancellationToken));
            }

            return new EngineEnqueueOutcome(null, 0);
        }

        RecordSkipReason(request.SkipReasonCodes, request.SkipReasons, enqueueDecision);
        if (request.UseAtmosStereoDual
            && !isVideo
            && !IsAtmosQuality(request.SelectedQuality)
            && ShouldContinueWithSecondaryAfterPrimarySkip(enqueueDecision))
        {
            await TryEnqueueAppleAtmosSecondaryAsync(
                new AppleSecondaryEnqueueRequest(
                    request.Intent,
                    request.Settings,
                    request.SecondaryDestinationFolderId,
                    request.Intent.AllowQualityUpgrade,
                    request.Queued,
                    request.Availability,
                    request.PreferIsrcOnly,
                    request.CancellationToken));
        }

        return new EngineEnqueueOutcome(null, 1);
    }

    private async Task<EngineEnqueueOutcome> EnqueueQobuzAsync(EngineEnqueueRequest request)
    {
        var hasQobuzUrl = !string.IsNullOrWhiteSpace(request.ResolvedSourceUrl)
            && request.ResolvedSourceUrl.Contains(QobuzDomain, StringComparison.OrdinalIgnoreCase);
        if (!hasQobuzUrl && !IsrcValidator.IsValid(request.Intent.Isrc))
        {
            return new EngineEnqueueOutcome(
                new DownloadIntentResult { Success = false, Message = "Valid ISRC required for Qobuz downloads.", Engine = request.Engine },
                0);
        }

        var payload = new QobuzQueueItem();
        PopulateStandardQueuePayload(payload, request.Intent, new StandardPayloadContext(
            request.ResolvedSourceUrl ?? string.Empty,
            string.IsNullOrWhiteSpace(request.Intent.Album) ? TrackType : AlbumType,
            ResolveContentType(request.Intent.ContentType, request.ResolvedSourceUrl, TrackType, request.Intent.HasAtmos, request.SelectedQuality),
            request.FallbackInfo.AutoSources,
            request.FallbackInfo.AutoIndex,
            request.FallbackInfo.FallbackPlan,
            string.Empty,
            request.DurationSeconds,
            request.PrimaryDestinationFolderId,
            request.UseAtmosStereoDual ? StereoType : string.Empty));
        payload.Quality = request.SelectedQuality ?? request.Settings.QobuzQuality ?? "27";
        ApplyIntentMetadata(payload, request.Intent);

        var skipped = await EnqueuePrimaryPayloadAsync(payload, CreatePrimaryEnqueueContext(request));
        return new EngineEnqueueOutcome(null, skipped);
    }

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
            deezerTrackId = NormalizeDeezerTrackId(TryExtractDeezerTrackId(resolvedSourceUrl));
        }

        return deezerTrackId;
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

    private async Task<(bool Applied, string? Error)> ApplyDownloadProfileOverridesAsync(
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
                return (false, "Destination music folder requires a valid AutoTag profile.");
            }

            var normalizedSource = DownloadTagSourceHelper.ResolveMetadataSource(
                profile.DownloadTagSource,
                intent.PreferredEngine,
                settings.Service,
                intent.SourceService);

            DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile, metadataSourceOverride: normalizedSource ?? string.Empty);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to apply download profile overrides for folder {FolderId}", destinationFolderId);
            return (false, "Failed to apply destination profile settings.");
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
            || string.IsNullOrWhiteSpace(appleId)
            || IsAppleStationId(appleId)
            || IsAppleStationUrl(sourceUrl))
        {
            return appleId;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var mediaUserToken = settings.AppleMusic?.MediaUserToken;

        if (isVideo)
        {
            return await ResolveAppleVideoIdOrFallbackAsync(appleId, storefront, cancellationToken);
        }

        var songResolved = await TryResolveAppleSongIdAsync(appleId, storefront, mediaUserToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(songResolved))
        {
            return songResolved;
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
            _logger.LogDebug(ex, "Apple catalog video lookup failed for {AppleId}", appleId);
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
            _logger.LogDebug(ex, "Apple catalog song lookup failed for {AppleId}", appleId);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Apple catalog song payload could not be parsed for {AppleId}", appleId);
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
            _logger.LogDebug(ex, "Apple catalog ISRC lookup failed for {Isrc}", isrc);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Apple catalog ISRC payload could not be parsed for {Isrc}", isrc);
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

    private async Task<DownloadIntentResult?> TryBlockByDownloadGateAsync(CancellationToken cancellationToken)
    {
        var downloadGate = await _orchestrationService.EvaluateDownloadGateAsync(cancellationToken);
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
            Engine = string.Empty
        };
    }

    private async Task<EnqueuePreparation> PrepareEnqueueAsync(DownloadIntent intent, CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        NormalizeEnqueueSettings(settings);
        var isPodcastIntent = NormalizeIntentContentType(intent);
        if (!intent.DestinationFolderId.HasValue)
        {
            intent.DestinationFolderId = await ResolveRoutedDestinationFolderIdAsync(intent, settings, cancellationToken);
        }

        return new EnqueuePreparation(settings, isPodcastIntent, ResolveMetadataDestinationFolderId(intent, settings));
    }

    private static void NormalizeEnqueueSettings(DeezSpoTagSettings settings)
    {
        if (string.Equals(settings.Service, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
        {
            settings.Service = AutoService;
        }

        if (string.Equals(settings.Service, AutoService, StringComparison.OrdinalIgnoreCase))
        {
            settings.FallbackBitrate = true;
        }

        settings.MetadataSource = NormalizeMetadataSource(settings.MetadataSource) ?? string.Empty;
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
            ? (routingMultiQuality!.PrimaryDestinationFolderId ?? intent.DestinationFolderId)
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
                Message = "Videos and Atmos downloads must use the Apple engine.",
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

    private async Task<EnqueueRoutingState> PrepareEnqueueRoutingAsync(DownloadIntent intent, EnqueuePreparation preparation, CancellationToken cancellationToken)
    {
        var settings = preparation.Settings;
        var normalizedRequestedContentType = NormalizeContentType(intent.ContentType);
        var explicitAtmosRequest = string.Equals(normalizedRequestedContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase);
        var explicitStereoRequest = string.Equals(normalizedRequestedContentType, DownloadContentTypes.Stereo, StringComparison.OrdinalIgnoreCase);
        if (explicitAtmosRequest && string.IsNullOrWhiteSpace(intent.Quality))
        {
            intent.Quality = AtmosQuality;
        }

        var targetQuality = string.IsNullOrWhiteSpace(intent.Quality) ? null : intent.Quality;
        var availability = await ResolveAvailabilityAsync(intent, cancellationToken);
        var multiQuality = settings.MultiQuality;
        var useMultiQuality = IsMultiQualityDualEnabled(multiQuality);
        if (useMultiQuality && !explicitAtmosRequest && IsMusicIntent(intent) && !intent.HasAtmos)
        {
            await TryHydrateAtmosCapabilityAsync(intent, availability, settings, cancellationToken);
        }

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

        var autoSources = preparation.IsPodcastIntent
            ? DownloadSourceOrder.ResolveEngineQualitySources(normalizedPreferredEngine, DownloadContentTypes.Podcast, strict: true)
            : DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: targetQuality);
        var preferredEngine = ResolvePreferredEngine(normalizedPreferredEngine, intentRequestsAuto, appleOnlyRequired, preparation.IsPodcastIntent, autoSources);
        targetQuality = NormalizeTargetQuality(intent, settings, preferredEngine, targetQuality, explicitStereoRequest, useAtmosStereoDual);
        if (useAtmosStereoDual)
        {
            autoSources = RemoveAtmosAutoSources(autoSources);
        }

        if (string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            intent.SpotifyId = TryExtractSpotifyId(intent.SourceUrl)
                ?? await _spotifyIdResolver.ResolveTrackIdAsync(
                    intent.Title ?? string.Empty,
                    intent.Artist ?? string.Empty,
                    intent.Album,
                    intent.Isrc,
                    cancellationToken)
                ?? string.Empty;
        }

        return new EnqueueRoutingState(normalizedPreferredEngine, intentRequestsAuto, appleOnlyRequired, autoSources, preferredEngine, targetQuality, availability, useAtmosStereoDual);
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

    private async Task<ResolvedEnqueueTarget?> ResolvePrimaryEnqueueTargetAsync(
        DownloadIntent intent,
        EnqueueRoutingState routing,
        DeezSpoTagSettings settings,
        bool preferIsrcOnly,
        CancellationToken cancellationToken)
    {
        var isAuto = !IsIntentPodcast(intent, intent.SourceUrl ?? string.Empty)
            && (routing.IntentRequestsAuto || string.Equals(settings.Service, AutoService, StringComparison.OrdinalIgnoreCase));
        var allowCrossEngineFallback = !IsIntentPodcast(intent, intent.SourceUrl ?? string.Empty) && isAuto;
        if (routing.AppleOnlyRequired)
        {
            return await ResolveAppleOnlyEnqueueTargetAsync(intent, routing, settings, preferIsrcOnly, cancellationToken);
        }

        if (isAuto)
        {
            return await ResolveAutoEnqueueTargetAsync(intent, routing, preferIsrcOnly, cancellationToken);
        }

        var resolved = await ResolveIntentAsync(intent, routing.PreferredEngine, preferIsrcOnly, routing.Availability, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolved.Message) && resolved.Engine == string.Empty)
        {
            return new ResolvedEnqueueTarget(string.Empty, routing.TargetQuality, 0, allowCrossEngineFallback, string.Empty, resolved, routing.AutoSources);
        }

        var availabilityWarning = !string.IsNullOrWhiteSpace(intent.PreferredEngine)
            && routing.Availability != null
            && string.IsNullOrWhiteSpace(GetAvailabilityUrl(routing.Availability, resolved.Engine))
                ? $"Availability check indicates {resolved.Engine} is unavailable for this track."
                : string.Empty;
        return new ResolvedEnqueueTarget(resolved.Engine, routing.TargetQuality, 0, allowCrossEngineFallback, availabilityWarning, resolved, routing.AutoSources);
    }

    private async Task<ResolvedEnqueueTarget?> ResolveAppleOnlyEnqueueTargetAsync(
        DownloadIntent intent,
        EnqueueRoutingState routing,
        DeezSpoTagSettings settings,
        bool preferIsrcOnly,
        CancellationToken cancellationToken)
    {
        var selectedQuality = routing.TargetQuality ?? ResolvePreferredQuality(settings, ApplePlatform);
        var autoSources = DownloadSourceOrder.ResolveEngineQualitySources(
            ApplePlatform,
            selectedQuality,
            strict: UseStrictQualityFallback(settings, ApplePlatform, selectedQuality));
        var candidate = await ResolveIntentAsync(intent, ApplePlatform, preferIsrcOnly, routing.Availability, cancellationToken);
        if (!string.IsNullOrWhiteSpace(candidate.Message) && candidate.Engine == string.Empty)
        {
            return string.IsNullOrWhiteSpace(candidate.Message) ? null : new ResolvedEnqueueTarget(
                string.Empty,
                selectedQuality,
                0,
                false,
                string.Empty,
                (string.Empty, null, candidate.Message, string.Empty),
                autoSources);
        }

        if (string.IsNullOrWhiteSpace(candidate.SourceUrl))
        {
            return new ResolvedEnqueueTarget(
                ApplePlatform,
                selectedQuality,
                0,
                false,
                string.Empty,
                (ApplePlatform, null, "Apple Music URL unavailable for Atmos or video downloads.", string.Empty),
                autoSources);
        }

        return new ResolvedEnqueueTarget(ApplePlatform, selectedQuality, 0, false, string.Empty, candidate, autoSources);
    }

    private async Task<ResolvedEnqueueTarget?> ResolveAutoEnqueueTargetAsync(
        DownloadIntent intent,
        EnqueueRoutingState routing,
        bool preferIsrcOnly,
        CancellationToken cancellationToken)
    {
        _activityLog.Info($"Auto mapping start: title='{intent.Title ?? string.Empty}' artist='{intent.Artist ?? string.Empty}' isrc='{intent.Isrc ?? string.Empty}'");
        var startIndex = ResolveAutoStartIndex(intent.PreferredEngine, routing.PreferredEngine, routing.AutoSources);
        for (var i = startIndex; i < routing.AutoSources.Count; i++)
        {
            var step = DownloadSourceOrder.DecodeAutoSource(routing.AutoSources[i]);
            if (!TryAcceptAutoResolutionCandidate(intent, step.Source, step.Quality ?? routing.TargetQuality, routing.Availability, out var skipReason))
            {
                _activityLog.Warn($"Auto mapping skip: engine={step.Source} quality={(step.Quality ?? routing.TargetQuality) ?? AutoService} reason={skipReason}");
                continue;
            }

            var candidate = await ResolveIntentAsync(intent, step.Source, preferIsrcOnly, routing.Availability, cancellationToken);
            if (!TryAcceptResolvedCandidate(step.Source, candidate, out skipReason))
            {
                _activityLog.Warn($"Auto mapping skip: engine={step.Source} quality={(step.Quality ?? routing.TargetQuality) ?? AutoService} reason={skipReason}");
                continue;
            }

            return new ResolvedEnqueueTarget(step.Source, step.Quality ?? routing.TargetQuality, i, true, string.Empty, candidate, routing.AutoSources);
        }

        return null;
    }

    private static int ResolveAutoStartIndex(string? preferredEngine, string resolvedPreferredEngine, IReadOnlyList<string> autoSources)
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

    private bool TryAcceptAutoResolutionCandidate(
        DownloadIntent intent,
        string candidateEngine,
        string? candidateQuality,
        SongLinkResult? availability,
        out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(candidateEngine))
        {
            reason = "missing_engine";
            return false;
        }

        _activityLog.Info($"Auto mapping try: engine={candidateEngine} quality={candidateQuality ?? AutoService}");
        if (availability != null && string.IsNullOrWhiteSpace(GetAvailabilityUrl(availability, candidateEngine)))
        {
            reason = "unavailable";
            return false;
        }

        if (string.Equals(candidateEngine, QobuzPlatform, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(intent.Isrc)
            && (string.IsNullOrWhiteSpace(intent.Title) || string.IsNullOrWhiteSpace(intent.Artist)))
        {
            reason = "missing_isrc_or_metadata";
            return false;
        }

        return true;
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
            && candidateEngine is DeezerPlatform or TidalPlatform or AmazonPlatform or ApplePlatform;
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
        IReadOnlyList<string> autoSources)
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
        if (IsMusicIntent(intent) && !IsAppleAtmosOnlyRequest(engine, selectedQuality))
        {
            return ApplyMusicResolvedQuality(settings, engine, selectedQuality);
        }

        if (!IsMusicIntent(intent))
        {
            return ResolveNonMusicResolvedQuality(intent, selectedQuality);
        }

        return selectedQuality;
    }

    private string? ApplyMusicResolvedQuality(DeezSpoTagSettings settings, string engine, string? selectedQuality)
    {
        var preflight = QualityFallbackManager.ApplyQualityFallback(engine, selectedQuality, settings);
        if (string.IsNullOrWhiteSpace(preflight.SelectedQuality))
        {
            return selectedQuality;
        }

        if (preflight.FallbackApplied)
        {
            var requested = string.IsNullOrWhiteSpace(preflight.RequestedQuality) ? AutoService : preflight.RequestedQuality;
            _activityLog.Info($"Preflight quality fallback: engine={engine} requested={requested} selected={preflight.SelectedQuality} reason={preflight.Reason ?? "fallback"}");
        }

        return preflight.SelectedQuality;
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

    private void LogResolvedIntentMapping((string Engine, string? SourceUrl, string Message, string MappingSource) resolved, string engine)
    {
        if (!string.IsNullOrWhiteSpace(resolved.MappingSource))
        {
            _activityLog.Info($"Intent mapping: engine={engine} source={resolved.MappingSource}");
        }
        else
        {
            _activityLog.Info($"Intent mapping: engine={engine} source=default");
        }
    }

    private async Task<DownloadIntentResult?> TryValidateEnqueueProfileAsync(DownloadIntent intent, EnqueuePreparation preparation, CancellationToken cancellationToken)
    {
        if (!IsMusicIntent(intent))
        {
            return null;
        }

        var profileResult = await ApplyDownloadProfileOverridesAsync(intent, preparation.Settings, preparation.MetadataDestinationFolderId, cancellationToken);
        if (profileResult.Applied)
        {
            return null;
        }

        return new DownloadIntentResult
        {
            Success = false,
            Engine = string.Empty,
            Message = profileResult.Error ?? "Destination music folder requires a valid AutoTag profile."
        };
    }

    private async Task<DownloadIntentResult?> TryBlockByGlobalBlocklistAsync(DownloadIntent intent, CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        try
        {
            var blocklistMatch = await _libraryRepository.FindMatchingDownloadBlocklistAsync(
                intent.Title,
                intent.Artist,
                intent.Album,
                cancellationToken);
            if (blocklistMatch == null)
            {
                return null;
            }

            var blockMessage = $"Skipped: blocked by global {blocklistMatch.Field} rule ({blocklistMatch.Value}).";
            _activityLog.Warn(blockMessage);
            _logger.LogInformation(
                "Download intent blocked by global blocklist ({Field}={Value}): {Title} - {Artist}",
                blocklistMatch.Field,
                blocklistMatch.Value,
                intent.Title,
                intent.Artist);
            return new DownloadIntentResult
            {
                Success = false,
                Engine = string.Empty,
                Message = blockMessage,
                Skipped = 1,
                SkipReasonCodes = new List<string> { "blocklist_match" },
                SkipReasons = new List<string> { blockMessage }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Global blocklist check failed; continuing enqueue flow.");
            return null;
        }
    }

    private async Task<(string Engine, string? SourceUrl, string Message, string MappingSource)> ResolveIntentAsync(
        DownloadIntent intent,
        string engine,
        bool preferIsrcOnly,
        SongLinkResult? preResolved,
        CancellationToken cancellationToken)
    {
        var bootstrap = BootstrapIntentResolution(intent);
        var sourceUrl = bootstrap.SourceUrl;
        var directResult = TryResolveDirectIntentSource(engine, sourceUrl, bootstrap.NormalizedDeezerId, bootstrap.IsPodcastIntent);
        if (directResult.HasValue)
        {
            return directResult.Value;
        }

        await TryHydrateIntentIsrcFromBootstrapAsync(intent, bootstrap);

        var settings = _settingsService.LoadSettings();
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

        SongLinkResult? songLink = await ResolveSongLinkForIntentAsync(
            intent,
            sourceUrl,
            bootstrap.NormalizedDeezerId,
            userCountry,
            preResolved,
            cancellationToken);
        await TryHydrateIntentIsrcFromSourceUrlAsync(intent, songLink, sourceUrl);

        var mappedResult = await TryResolveViaSongLinkAsync(
            intent,
            songLink,
            engine,
            sourceUrl,
            userCountry,
            "songlink",
            cancellationToken);
        if (mappedResult.HasValue)
        {
            return mappedResult.Value;
        }

        songLink = await TryResolveFallbackSongLinkAsync(intent, engine, settings, resolverStrictMode, userCountry, songLink, cancellationToken);
        mappedResult = await TryResolveViaSongLinkAsync(
            intent,
            songLink,
            engine,
            sourceUrl,
            userCountry,
            "songlink-fallback-search",
            cancellationToken);
        if (mappedResult.HasValue)
        {
            return mappedResult.Value;
        }

        await TryHydrateQobuzIntentIsrcAsync(intent, songLink, engine, settings, cancellationToken);

        var engineSpecificResult = await TryResolveEngineSpecificIntentUrlAsync(intent, engine, cancellationToken);
        if (engineSpecificResult.HasValue)
        {
            return engineSpecificResult.Value;
        }

        var mismatchResult = BuildMismatchedEngineResolution(engine, sourceUrl, intent.Isrc);
        if (mismatchResult.HasValue)
        {
            return mismatchResult.Value;
        }

        return (engine, sourceUrl, string.Empty, string.Empty);
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
        SongLinkResult? songLink,
        string sourceUrl)
    {
        if (songLink != null || !string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return;
        }

        var deezerTrackId = TryExtractDeezerTrackId(sourceUrl);
        if (!string.IsNullOrWhiteSpace(deezerTrackId))
        {
            intent.Isrc = await ResolveDeezerIsrcAsync(deezerTrackId) ?? string.Empty;
        }
    }

    private async Task<(string Engine, string? SourceUrl, string Message, string MappingSource)?> TryResolveViaSongLinkAsync(
        DownloadIntent intent,
        SongLinkResult? songLink,
        string engine,
        string sourceUrl,
        string userCountry,
        string mappingSource,
        CancellationToken cancellationToken)
    {
        if (songLink == null)
        {
            return null;
        }

        var mapped = await TryResolveSongLinkMappingAsync(
            intent,
            songLink,
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
        SongLinkResult? songLink,
        string engine,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return;
        }

        var deezerId = await ResolveQobuzDeezerIdAsync(intent, songLink, cancellationToken);
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
        SongLinkResult? songLink,
        CancellationToken cancellationToken)
    {
        var deezerId = string.IsNullOrWhiteSpace(intent.DeezerId) ? string.Empty : intent.DeezerId;
        if (songLink != null)
        {
            deezerId = !string.IsNullOrWhiteSpace(songLink.DeezerId)
                ? songLink.DeezerId
                : TryExtractDeezerTrackId(songLink.DeezerUrl ?? string.Empty) ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            return deezerId;
        }

        var spotifyId = string.IsNullOrWhiteSpace(intent.SpotifyId)
            ? songLink?.SpotifyId
            : intent.SpotifyId;
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return string.Empty;
        }

        return await _songLinkResolver.ResolveDeezerIdFromSpotifyAsync(spotifyId, cancellationToken) ?? string.Empty;
    }

    private async Task<(string Engine, string? SourceUrl, string Message, string MappingSource)?> TryResolveEngineSpecificIntentUrlAsync(
        DownloadIntent intent,
        string engine,
        CancellationToken cancellationToken)
    {
        if (string.Equals(engine, TidalPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var durationSeconds = intent.DurationMs > 0 ? (int)Math.Round(intent.DurationMs / 1000d) : 0;
            var tidalUrl = await _tidalDownloadService.ResolveTrackUrlAsync(
                intent.Title ?? string.Empty,
                intent.Artist ?? string.Empty,
                intent.Isrc ?? string.Empty,
                durationSeconds,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(tidalUrl))
            {
                return (engine, tidalUrl, string.Empty, "tidal-search");
            }
        }

        if (!string.Equals(engine, ApplePlatform, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var appleUrl = await ResolveAppleSongUrlAsync(intent, cancellationToken);
        return string.IsNullOrWhiteSpace(appleUrl)
            ? null
            : (engine, appleUrl, string.Empty, "apple-search");
    }

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

    private async Task<SongLinkResult?> ResolveAvailabilityAsync(DownloadIntent intent, CancellationToken cancellationToken)
    {
        var userCountry = _settingsService.LoadSettings().DeezerCountry;
        var songLink = await ResolveInitialAvailabilityAsync(intent, userCountry, cancellationToken);
        ApplyAvailabilityIdentity(intent, songLink);
        return await ApplyQobuzAvailabilityFallbackAsync(intent, songLink, cancellationToken);
    }

    private async Task<SongLinkResult?> ResolveInitialAvailabilityAsync(
        DownloadIntent intent,
        string? userCountry,
        CancellationToken cancellationToken)
    {
        SongLinkResult? songLink = null;
        var normalizedDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
        if (!string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            var deezerUrl = $"https://www.deezer.com/track/{normalizedDeezerId}";
            songLink = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, userCountry, cancellationToken);
        }

        if (songLink == null && !string.IsNullOrWhiteSpace(intent.SourceUrl))
        {
            songLink = await _songLinkResolver.ResolveByUrlAsync(intent.SourceUrl, userCountry, cancellationToken);
        }

        if (songLink == null && !string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            songLink = await _songLinkResolver.ResolveSpotifyTrackAsync(intent.SpotifyId, cancellationToken);
        }

        return songLink;
    }

    private static void ApplyAvailabilityIdentity(DownloadIntent intent, SongLinkResult? songLink)
    {
        if (songLink == null)
        {
            return;
        }

        intent.Isrc = string.IsNullOrWhiteSpace(intent.Isrc) ? songLink.Isrc ?? string.Empty : intent.Isrc;
        if (string.IsNullOrWhiteSpace(intent.SpotifyId) && !string.IsNullOrWhiteSpace(songLink.SpotifyId))
        {
            intent.SpotifyId = songLink.SpotifyId;
        }
    }

    private async Task<SongLinkResult?> ApplyQobuzAvailabilityFallbackAsync(
        DownloadIntent intent,
        SongLinkResult? songLink,
        CancellationToken cancellationToken)
    {
        if (!CanResolveQobuzByMetadata(intent)
            || (songLink != null && !string.IsNullOrWhiteSpace(songLink.QobuzUrl)))
        {
            return songLink;
        }

        if (songLink == null && !string.IsNullOrWhiteSpace(intent.Isrc))
        {
            return null;
        }

        var qobuzUrl = await _songLinkResolver.ResolveQobuzUrlByMetadataAsync(
            intent.Title,
            intent.Artist,
            intent.DurationMs > 0 ? intent.DurationMs : null,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(qobuzUrl))
        {
            LogQobuzFallbackMiss(intent, songLink);
            return songLink;
        }

        if (songLink == null)
        {
            songLink = new SongLinkResult();
            _activityLog.Info($"Qobuz fallback hit (no songlink): title='{intent.Title}' artist='{intent.Artist}' url='{qobuzUrl}'");
        }
        else
        {
            _activityLog.Info($"Qobuz fallback hit: title='{intent.Title}' artist='{intent.Artist}' url='{qobuzUrl}'");
        }

        songLink.QobuzUrl = qobuzUrl;
        return songLink;
    }

    private void LogQobuzFallbackMiss(DownloadIntent intent, SongLinkResult? songLink)
    {
        if (songLink == null)
        {
            _activityLog.Warn($"Qobuz fallback miss (no songlink): title='{intent.Title}' artist='{intent.Artist}' isrc='{intent.Isrc}'");
            return;
        }

        _activityLog.Warn($"Qobuz fallback miss: title='{intent.Title}' artist='{intent.Artist}' isrc='{intent.Isrc}'");
    }

    private static bool CanResolveQobuzByMetadata(DownloadIntent intent)
    {
        return !string.IsNullOrWhiteSpace(intent.Title)
            && !string.IsNullOrWhiteSpace(intent.Artist);
    }

    private async Task TryHydrateAtmosCapabilityAsync(
        DownloadIntent intent,
        SongLinkResult? availability,
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

    private static string? GetAvailabilityUrl(SongLinkResult availability, string engine)
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

    private async Task<(bool Resolved, string Url, string MappingSource)> TryResolveSongLinkMappingAsync(
        DownloadIntent intent,
        SongLinkResult songLink,
        string engine,
        string sourceUrl,
        string userCountry,
        string mappingSource,
        CancellationToken cancellationToken)
    {
        intent.Isrc = string.IsNullOrWhiteSpace(intent.Isrc) ? songLink.Isrc ?? string.Empty : intent.Isrc;
        if (string.IsNullOrWhiteSpace(intent.SpotifyId) && !string.IsNullOrWhiteSpace(songLink.SpotifyId))
        {
            intent.SpotifyId = songLink.SpotifyId;
        }

        var mappedUrl = ResolveSongLinkMappedUrl(songLink, engine, sourceUrl);
        if (string.Equals(engine, AmazonPlatform, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(mappedUrl))
        {
            var amazonUrl = await ResolveAmazonSongLinkFallbackAsync(intent, songLink, userCountry, cancellationToken);
            if (!string.IsNullOrWhiteSpace(amazonUrl))
            {
                return (true, amazonUrl, SonglinkSpotifyKey);
            }
        }

        if (!string.Equals(engine, QobuzPlatform, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(mappedUrl))
        {
            return (true, mappedUrl ?? string.Empty, mappingSource);
        }

        return (false, string.Empty, string.Empty);
    }

    private static string? ResolveSongLinkMappedUrl(SongLinkResult songLink, string engine, string sourceUrl)
        => engine switch
        {
            DeezerPlatform => songLink.DeezerUrl,
            ApplePlatform => songLink.AppleMusicUrl,
            TidalPlatform => songLink.TidalUrl,
            AmazonPlatform => songLink.AmazonUrl,
            QobuzPlatform => songLink.QobuzUrl,
            _ => sourceUrl
        };

    private async Task<string?> ResolveAmazonSongLinkFallbackAsync(
        DownloadIntent intent,
        SongLinkResult songLink,
        string userCountry,
        CancellationToken cancellationToken)
    {
        var spotifyId = string.IsNullOrWhiteSpace(intent.SpotifyId)
            ? songLink.SpotifyId
            : intent.SpotifyId;
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            var spotifyLink = await _songLinkResolver.ResolveSpotifyTrackAsync(spotifyId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(spotifyLink?.AmazonUrl))
            {
                return spotifyLink.AmazonUrl;
            }
        }

        if (!string.IsNullOrWhiteSpace(songLink.SpotifyUrl))
        {
            var spotifyLink = await _songLinkResolver.ResolveByUrlAsync(songLink.SpotifyUrl, userCountry, cancellationToken);
            if (!string.IsNullOrWhiteSpace(spotifyLink?.AmazonUrl))
            {
                return spotifyLink.AmazonUrl;
            }
        }

        return null;
    }

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
            _logger.LogDebug(ex, "Apple MV metadata lookup failed for {Url}", sourceUrl);
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
        return long.TryParse(id, out _) ? id : null;
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
            _logger.LogDebug(ex, "Deezer GW ISRC lookup failed for {TrackId}", trackId);
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
            _logger.LogDebug(ex, "Deezer API ISRC lookup failed for {TrackId}", trackId);
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
            _songLinkResolver,
            BuildSpotifyTrackSummary(intent),
            new SpotifyTrackResolveOptions(
                AllowFallbackSearch: fallbackSearch,
                PreferIsrcOnly: false,
                UseSongLink: true,
                StrictMode: strictMode,
                BypassNegativeCanonicalCache: false,
                Logger: _logger,
                CancellationToken: cancellationToken));
        return NormalizeDeezerTrackId(resolvedDeezerId);
    }

    private static string? NormalizeDeezerTrackId(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        return long.TryParse(trackId, out _) ? trackId : null;
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
            return await _songLinkResolver.ResolveQobuzUrlByIsrcAsync(intent.Isrc, cancellationToken);
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
                _logger.LogDebug(ex, "Deezer ISRC URL resolve failed for {Isrc}", intent.Isrc);
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
            _logger.LogWarning(ex, "Apple catalog search failed for {Title} - {Artist}", intent.Title, intent.Artist);
            return null;
        }
    }

    private async Task PopulateIntentMetadataAsync(DownloadIntent intent, DeezSpoTagSettings settings, CancellationToken cancellationToken)
    {
        var sourceUrl = intent.SourceUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceUrl) && !string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            sourceUrl = $"https://open.spotify.com/track/{intent.SpotifyId}";
        }
        var isBoomplaySource = BoomplayMetadataService.IsBoomplayUrl(sourceUrl)
            || string.Equals(intent.SourceService, "boomplay", StringComparison.OrdinalIgnoreCase);

        var metadataSource = NormalizeMetadataSource(settings.MetadataSource);
        if (!string.IsNullOrWhiteSpace(metadataSource))
        {
            await PopulatePreferredMetadataSourceAsync(intent, metadataSource, sourceUrl, cancellationToken);
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
        string metadataSource,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        if (string.Equals(metadataSource, SpotifyPlatform, StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(metadataSource, DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureDeezerIdentityAsync(intent, sourceUrl, cancellationToken);

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

        var spotifyId = TryExtractSpotifyId(sourceUrl);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            intent.SpotifyId = spotifyId;
            return;
        }

        if (await TryPopulateSpotifyIdentityFromDeezerAsync(intent, cancellationToken))
        {
            return;
        }

        await PopulateSpotifyIdentityFromSourceUrlAsync(intent, sourceUrl, cancellationToken);

        if (string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            intent.SpotifyId = await _spotifyIdResolver.ResolveTrackIdAsync(
                intent.Title ?? string.Empty,
                intent.Artist ?? string.Empty,
                intent.Album,
                intent.Isrc,
                cancellationToken) ?? string.Empty;
        }
    }

    private async Task EnsureDeezerIdentityAsync(DownloadIntent intent, string sourceUrl, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            return;
        }

        var deezerId = TryExtractDeezerTrackId(sourceUrl);
        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            intent.DeezerId = deezerId;
            return;
        }

        if (string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            intent.SpotifyId = TryExtractSpotifyId(sourceUrl)
                ?? await _spotifyIdResolver.ResolveTrackIdAsync(
                    intent.Title ?? string.Empty,
                    intent.Artist ?? string.Empty,
                    intent.Album,
                    intent.Isrc,
                    cancellationToken)
                ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            intent.DeezerId = await _songLinkResolver.ResolveDeezerIdFromSpotifyAsync(intent.SpotifyId, cancellationToken) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(intent.DeezerId) && !string.IsNullOrWhiteSpace(sourceUrl))
        {
            var link = await _songLinkResolver.ResolveByUrlAsync(sourceUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(link?.DeezerId))
            {
                intent.DeezerId = link.DeezerId;
            }
            if (string.IsNullOrWhiteSpace(intent.SpotifyId) && !string.IsNullOrWhiteSpace(link?.SpotifyId))
            {
                intent.SpotifyId = link.SpotifyId;
            }
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

    private static string? NormalizeMetadataSource(string? metadataSource)
    {
        return DownloadTagSourceHelper.NormalizeMetadataResolverSource(metadataSource);
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
        var normalizedValues = GenreTagAliasNormalizer.NormalizeAndExpandValues(values, aliasMap, _genreTagNormalizationEnabled);
        var output = new List<string>(normalizedValues.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in normalizedValues)
        {
            if (string.IsNullOrWhiteSpace(genre) || BlockedGenres.Contains(genre))
            {
                continue;
            }

            if (seen.Add(genre))
            {
                output.Add(genre);
            }
        }

        return output;
    }

    private IReadOnlyDictionary<string, string> GetGenreAliasMap()
    {
        if (_genreAliasMap != null)
        {
            return _genreAliasMap;
        }

        var settings = _settingsService.LoadSettings();
        _genreTagNormalizationEnabled = settings.NormalizeGenreTags;
        _genreAliasMap = settings.NormalizeGenreTags
            ? GenreTagAliasNormalizer.BuildAliasMap(settings.GenreTagAliasRules)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return _genreAliasMap;
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

            ApplyIntentNullableValue(overwriteExisting, intent.Danceability, summary?.Danceability, value => intent.Danceability = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Energy, summary?.Energy, value => intent.Energy = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Valence, summary?.Valence, value => intent.Valence = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Acousticness, summary?.Acousticness, value => intent.Acousticness = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Instrumentalness, summary?.Instrumentalness, value => intent.Instrumentalness = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Speechiness, summary?.Speechiness, value => intent.Speechiness = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Loudness, summary?.Loudness, value => intent.Loudness = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Tempo, summary?.Tempo, value => intent.Tempo = value);
            ApplyIntentNullableValue(overwriteExisting, intent.TimeSignature, summary?.TimeSignature, value => intent.TimeSignature = value);
            ApplyIntentNullableValue(overwriteExisting, intent.Liveness, summary?.Liveness, value => intent.Liveness = value);

            var mappedKey = SpotifyAudioFeatureMapper.MapKey(summary?.Key, summary?.Mode);
            ApplyIntentStringValue(overwriteExisting, intent.MusicKey, mappedKey, value => intent.MusicKey = value);

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
            _logger.LogDebug(ex, "Spotify metadata lookup failed for intent url {Url}", sourceUrl);
        }
    }

    private static bool IsIntentPodcast(DownloadIntent intent, string sourceUrl)
    {
        return string.Equals(NormalizeContentType(intent.ContentType), DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase)
            || IsPodcastSource(sourceUrl, null);
    }

    private static string? BootstrapIntentDeezerIdentity(DownloadIntent intent, string sourceUrl, bool isPodcastIntent, ref string normalizedSourceUrl)
    {
        if (string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            intent.DeezerId = isPodcastIntent
                ? (TryExtractDeezerEpisodeId(sourceUrl) ?? string.Empty)
                : (TryExtractDeezerTrackId(sourceUrl) ?? string.Empty);
        }

        var normalizedDeezerId = NormalizeDeezerTrackId(intent.DeezerId);
        if (string.IsNullOrWhiteSpace(normalizedSourceUrl) && !string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            normalizedSourceUrl = isPodcastIntent
                ? $"https://www.deezer.com/episode/{normalizedDeezerId}"
                : $"https://www.deezer.com/track/{normalizedDeezerId}";
        }

        return normalizedDeezerId;
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

    private async Task<SongLinkResult?> ResolveSongLinkForIntentAsync(
        DownloadIntent intent,
        string sourceUrl,
        string? normalizedDeezerId,
        string? userCountry,
        SongLinkResult? preResolved,
        CancellationToken cancellationToken)
    {
        SongLinkResult? songLink = preResolved;
        if (songLink == null && !string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            songLink = await _songLinkResolver.ResolveByUrlAsync($"https://www.deezer.com/track/{normalizedDeezerId}", userCountry, cancellationToken);
        }
        if (songLink == null && !string.IsNullOrWhiteSpace(sourceUrl))
        {
            songLink = await _songLinkResolver.ResolveByUrlAsync(sourceUrl, userCountry, cancellationToken);
        }
        if (songLink == null && !string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            songLink = await _songLinkResolver.ResolveSpotifyTrackAsync(intent.SpotifyId, cancellationToken);
        }
        if (songLink == null && string.IsNullOrWhiteSpace(intent.SpotifyId))
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
                songLink = await _songLinkResolver.ResolveSpotifyTrackAsync(spotifyId, cancellationToken);
            }
        }
        return songLink;
    }

    private async Task<SongLinkResult?> TryResolveFallbackSongLinkAsync(
        DownloadIntent intent,
        string engine,
        DeezSpoTagSettings settings,
        bool resolverStrictMode,
        string? userCountry,
        SongLinkResult? songLink,
        CancellationToken cancellationToken)
    {
        if (songLink != null
            || !settings.FallbackSearch
            || (engine != SpotifyPlatform && engine != DeezerPlatform && engine != ApplePlatform))
        {
            return songLink;
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
            return await _songLinkResolver.ResolveByUrlAsync($"https://www.deezer.com/track/{normalized}", userCountry, cancellationToken);
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
            _logger.LogDebug(ex, "Apple metadata lookup failed for intent url {Url}", sourceUrl);
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

            if (string.IsNullOrWhiteSpace(intent.Cover))
            {
                intent.Cover = AppleCatalogVideoAttributeParser.ResolveArtwork(attrs, 1200);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple station metadata lookup failed for {StationId}", stationId);
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
            _logger.LogDebug(ex, "Boomplay metadata lookup failed for intent url {Url}", sourceUrl);
        }
    }

    private async Task PopulateDeezerMetadataAsync(
        DownloadIntent intent,
        string sourceUrl,
        bool overwriteExisting = false,
        bool forceCoverOverwrite = false)
    {
        var trackId = string.IsNullOrWhiteSpace(intent.DeezerId)
            ? TryExtractDeezerTrackId(sourceUrl)
            : intent.DeezerId;
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

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
            _logger.LogDebug(ex, "Deezer metadata lookup failed for intent url {Url}", sourceUrl);
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
        var hasSongLinkInputs = !string.IsNullOrWhiteSpace(sourceUrl)
            || !string.IsNullOrWhiteSpace(intent.DeezerId)
            || !string.IsNullOrWhiteSpace(intent.SpotifyId);
        var requiredInputsSnapshot = BuildRequiredInputsSnapshot(intent, sourceUrl);

        AppendFallbackPlanSteps(
            steps,
            planSources,
            sourceUrl,
            intent.Isrc,
            hasSongLinkInputs,
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
        bool hasSongLinkInputs,
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

        if (hasSongLinkInputs)
        {
            return "songlink_url";
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
        bool hasSongLinkInputs,
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
                hasSongLinkInputs,
                fallbackSearchEnabled);

            steps.Add(new FallbackPlanStep(
                StepId: $"step-{index++}",
                Engine: decoded.Source,
                Quality: decoded.Quality,
                RequiredInputs: requiredInputsSnapshot.ToList(),
                ResolutionStrategy: resolutionStrategy));
        }
    }

    private static List<string> BuildFallbackPlanSources(
        IReadOnlyList<string> autoSources,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string engine,
        string? requestedQuality)
    {
        var planSources = new List<string>();
        var strict = UseStrictQualityFallback(settings, engine, requestedQuality);
        var isAuto = string.Equals(settings.Service, AutoService, StringComparison.OrdinalIgnoreCase);

        if (isAuto)
        {
            // Always use the full AutoPriority quality-descending fallback order.
            // The autoIndex on the payload determines the starting position.
            planSources.AddRange(
                DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null));
        }
        else
        {
            AppendEngineFallbackSources(planSources, autoSources, settings, engine, requestedQuality, strict);
        }

        if (planSources.Count == 0 && autoSources.Count > 0)
        {
            planSources.AddRange(autoSources);
        }

        return DownloadSourceOrder.CollapseAutoSourcesByService(planSources);
    }

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
        await EnsureDeezerIdentityAsync(intent, sourceUrl, cancellationToken);
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
        }
    }

    private static bool IsSpotifySourceUrl(string sourceUrl)
    {
        return sourceUrl.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryPopulateSpotifyIdentityFromDeezerAsync(DownloadIntent intent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            return false;
        }

        var link = await _songLinkResolver.ResolveByDeezerTrackIdAsync(intent.DeezerId, cancellationToken);
        if (string.IsNullOrWhiteSpace(link?.SpotifyId))
        {
            return false;
        }

        intent.SpotifyId = link.SpotifyId;
        if (string.IsNullOrWhiteSpace(intent.DeezerId) && !string.IsNullOrWhiteSpace(link.DeezerId))
        {
            intent.DeezerId = link.DeezerId;
        }

        return true;
    }

    private async Task PopulateSpotifyIdentityFromSourceUrlAsync(DownloadIntent intent, string sourceUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        var link = await _songLinkResolver.ResolveByUrlAsync(sourceUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(link?.SpotifyId))
        {
            intent.SpotifyId = link.SpotifyId;
        }

        if (string.IsNullOrWhiteSpace(intent.DeezerId) && !string.IsNullOrWhiteSpace(link?.DeezerId))
        {
            intent.DeezerId = link.DeezerId;
        }
    }

    private static void AppendEngineFallbackSources(
        List<string> planSources,
        IReadOnlyList<string> autoSources,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string engine,
        string? requestedQuality,
        bool strict)
    {
        if (!string.IsNullOrWhiteSpace(engine))
        {
            planSources.AddRange(DownloadSourceOrder.ResolveEngineQualitySources(engine, requestedQuality, strict));
        }

        var seenEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(engine))
        {
            seenEngines.Add(engine);
        }

        foreach (var decoded in autoSources.Select(DownloadSourceOrder.DecodeAutoSource))
        {
            if (string.IsNullOrWhiteSpace(decoded.Source) || seenEngines.Contains(decoded.Source))
            {
                continue;
            }

            seenEngines.Add(decoded.Source);
            var preferredQuality = string.IsNullOrWhiteSpace(decoded.Quality)
                ? ResolvePreferredQuality(settings, decoded.Source)
                : decoded.Quality;
            planSources.AddRange(DownloadSourceOrder.ResolveEngineQualitySources(decoded.Source, preferredQuality, strict));
        }
    }

    private static readonly Dictionary<string, int> CanonicalQualityRanks =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ATMOS"] = 130,
            ["VIDEO"] = 125,
            ["27"] = 120,
            ["HI_RES_LOSSLESS"] = 115,
            ["ALAC"] = 110,
            ["7"] = 100,
            ["6"] = 90,
            ["LOSSLESS"] = 80,
            ["FLAC"] = 70,
            ["9"] = 60,
            ["AAC"] = 50,
            ["3"] = 40,
            ["1"] = 30
        };

    private static readonly Dictionary<string, int> LocalQualityRanks =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ATMOS"] = 5,
            ["VIDEO"] = 0,
            ["27"] = 4,
            ["HI_RES_LOSSLESS"] = 4,
            ["ALAC"] = 3,
            ["7"] = 4,
            ["6"] = 3,
            ["LOSSLESS"] = 3,
            ["FLAC"] = 3,
            ["9"] = 3,
            ["AAC"] = 2,
            ["3"] = 2,
            ["1"] = 1
        };

    private static int? ParseRequestedQualityRank(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return null;
        }

        var normalized = quality.Trim();
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
        string? preferred = normalized switch
        {
            ApplePlatform => settings.AppleMusic?.PreferredAudioProfile,
            DeezerPlatform => settings.MaxBitrate > 0 ? settings.MaxBitrate.ToString() : null,
            TidalPlatform => settings.TidalQuality,
            QobuzPlatform => settings.QobuzQuality,
            AmazonPlatform => "FLAC",
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

    private static string NormalizeEngineName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static bool IsKnownDownloadEngine(string? engine)
    {
        return engine is DeezerPlatform or ApplePlatform or TidalPlatform or AmazonPlatform or QobuzPlatform;
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

        if (string.Equals(intent.ContentType, DownloadContentTypes.Atmos, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsAtmosQuality(targetQuality))
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
        SongLinkResult? availability,
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
        SongLinkResult? availability,
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
            return availability?.AppleMusicUrl;
        }

        if (string.IsNullOrWhiteSpace(intent.AppleId))
        {
            return null;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? "us"
            : settings.AppleMusic!.Storefront;
        return $"https://music.apple.com/{storefront}/song/id{intent.AppleId}";
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

    private async Task<bool> TryEnqueueAppleAtmosSecondaryAsync(AppleSecondaryEnqueueRequest request)
    {
        var secondaryDestinationFolderId =
            request.SecondaryDestinationFolderId
            ?? request.Settings.MultiQuality?.SecondaryDestinationFolderId
            ?? request.Intent.SecondaryDestinationFolderId
            ?? request.Intent.DestinationFolderId;
        if (secondaryDestinationFolderId is null)
        {
            _logger.LogWarning(
                "Multi-quality secondary skipped: secondary destination folder is required for Apple Atmos.");
            return false;
        }

        var secondaryQuality = ResolveSecondaryQuality(ApplePlatform, null, excludeAtmos: false);
        if (string.IsNullOrWhiteSpace(secondaryQuality) || !IsAtmosQuality(secondaryQuality))
        {
            secondaryQuality = "ATMOS";
        }

        var candidate = await ResolveIntentAsync(
            request.Intent,
            ApplePlatform,
            request.PreferIsrcOnly,
            request.Availability,
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
        var fallbackInfo = BuildEnqueueFallbackInfo(new EnqueueFallbackRequest(
            request.Intent,
            request.Settings,
            ApplePlatform,
            secondaryQuality,
            MusicIntent: IsMusicIntent(request.Intent),
            AllowCrossEngineFallback: false,
            UseAtmosStereoDual: false,
            AutoSources: DownloadSourceOrder.ResolveEngineQualitySources(
                ApplePlatform,
                secondaryQuality,
                strict: UseStrictQualityFallback(request.Settings, ApplePlatform, secondaryQuality))));
        payload.FallbackPlan = fallbackInfo.FallbackPlan;
        payload.AutoSources = fallbackInfo.AutoSources;
        payload.AutoIndex = fallbackInfo.AutoIndex;

        var enqueueDecision = await EnqueueItemAsync(
            payload,
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

    private async Task<EnqueueItemDecision> EnqueueItemAsync<TPayload>(
        TPayload payload,
        bool allowQualityUpgrade,
        int? requestedQualityRank,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        if (payload == null)
        {
            return EnqueueItemDecision.Fail("invalid_payload", "Queue payload is missing.");
        }

        var context = BuildEnqueueItemContext(payload, allowQualityUpgrade, requestedQualityRank);
        var destinationFailure = await TryValidateEnqueueDestinationAsync(payload, context, cancellationToken);
        if (destinationFailure != null)
        {
            return destinationFailure;
        }

        var libraryFailure = await TryValidateLibraryDuplicateStateAsync(context, cancellationToken);
        if (libraryFailure != null)
        {
            return libraryFailure;
        }

        var duplicateResolution = await ResolveQueueDuplicateAsync(payload, context, cancellationToken);
        if (duplicateResolution.Decision != null)
        {
            return duplicateResolution.Decision;
        }
        if (!duplicateResolution.AllowInsert)
        {
            return EnqueueItemDecision.Fail("queue_duplicate", "Skipped: matching track is already in queue.");
        }

        return await InsertQueueItemAsync(payload, context, cancellationToken);
    }

    private EnqueueItemContext BuildEnqueueItemContext<TPayload>(
        TPayload payload,
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
            allowQualityUpgrade,
            requestedQualityRank,
            allowQualityUpgrade && requestedQualityRank.HasValue,
            requestedQualityRank ?? int.MinValue,
            requestedLocalQualityRank,
            allowQualityUpgrade && requestedLocalQualityRank.HasValue);
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

    private async Task<EnqueueItemDecision?> TryValidateLibraryDuplicateStateAsync(
        EnqueueItemContext context,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        var localUpgradeEligible = false;
        if (context.LocalQualityUpgradeRequested)
        {
            var bestLocalQualityRank = await _libraryRepository.GetBestLocalQualityRankAsync(
                context.Identity.TrackArtist,
                context.Identity.TrackTitle,
                context.Identity.DurationMs,
                artistPrimaryName: context.Identity.TrackPrimaryArtist,
                cancellationToken: cancellationToken);
            if (bestLocalQualityRank.HasValue && context.RequestedLocalQualityRank!.Value <= bestLocalQualityRank.Value)
            {
                return EnqueueItemDecision.Fail(
                    "library_quality_not_higher",
                    "Skipped: requested quality is not higher than the file already in your library.");
            }

            localUpgradeEligible = bestLocalQualityRank.HasValue && context.RequestedLocalQualityRank!.Value > bestLocalQualityRank.Value;
        }

        if (localUpgradeEligible
            || !await IsLibraryDuplicateAsync(
                new LibraryDuplicateCheck(
                    context.Identity.Isrc,
                    context.Identity.DeezerTrackId,
                    context.Identity.DeezerAlbumId,
                    context.Identity.DeezerArtistId,
                    context.Identity.SpotifyTrackId,
                    context.Identity.SpotifyAlbumId,
                    context.Identity.SpotifyArtistId,
                    context.Identity.AppleTrackId,
                    context.Identity.AppleAlbumId,
                    context.Identity.AppleArtistId,
                    context.Identity.TrackTitle,
                    context.Identity.DestinationFolderId,
                    context.Identity.RequestedAudioVariant,
                    cancellationToken)))
        {
            return null;
        }

        var message = context.LocalQualityUpgradeRequested
            ? "Skipped: matching file already exists in library and no eligible quality upgrade was found."
            : "Skipped: matching file already exists in library.";
        return EnqueueItemDecision.Fail("library_duplicate", message);
    }

    private async Task<QueueDuplicateResolution> ResolveQueueDuplicateAsync<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        var duplicateExists = await _queueRepository.ExistsDuplicateAsync(
            BuildDuplicateLookupRequest(context),
            cancellationToken);
        if (!duplicateExists)
        {
            return new QueueDuplicateResolution(null, true);
        }

        var existing = await _queueRepository.GetByMetadataAsync(
            BuildMetadataLookupRequest(context),
            cancellationToken);
        if (existing == null)
        {
            return new QueueDuplicateResolution(
                EnqueueItemDecision.Fail("queue_duplicate", "Skipped: matching track is already in queue."),
                false);
        }

        var status = existing.Status ?? string.Empty;
        if (IsRetryableFailedQueueStatus(status))
        {
            var decision = await RequeueFailedDuplicateAsync(payload, context, existing, cancellationToken);
            return new QueueDuplicateResolution(decision, false);
        }

        var isCompletedStatus = IsCompletedQueueStatus(status);
        if (isCompletedStatus)
        {
            if (!context.AllowQualityUpgrade || !context.QueueQualityUpgradeRequested)
            {
                return new QueueDuplicateResolution(
                    EnqueueItemDecision.Fail("queue_recently_downloaded", BuildCooldownMessage(context.Settings.RedownloadCooldownMinutes)),
                    false);
            }

            var existingRank = existing.QualityRank;
            if (existingRank.HasValue && context.RequestedRank <= existingRank.Value)
            {
                return new QueueDuplicateResolution(
                    EnqueueItemDecision.Fail(
                        "queue_quality_not_higher",
                        $"Skipped: queue already has this track at same or higher quality (status={status})."),
                    false);
            }

            return new QueueDuplicateResolution(null, true);
        }

        if (!isCompletedStatus && context.QueueQualityUpgradeRequested)
        {
            var upgradeResolution = await TryResolveQueuedQualityUpgradeAsync(
                payload,
                context,
                existing,
                status,
                cancellationToken);
            if (upgradeResolution.Decision != null || upgradeResolution.AllowInsert)
            {
                return upgradeResolution;
            }
        }

        return new QueueDuplicateResolution(
            EnqueueItemDecision.Fail(
                "queue_duplicate",
                $"Skipped: matching track is already in queue (status={status})."),
            false);
    }

    private async Task<QueueDuplicateResolution> TryResolveQueuedQualityUpgradeAsync<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        DownloadQueueItem existing,
        string status,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        var existingRank = existing.QualityRank;
        var hasSameOrHigherQueued = existingRank.HasValue && context.RequestedRank <= existingRank.Value;
        if (hasSameOrHigherQueued)
        {
            return new QueueDuplicateResolution(
                EnqueueItemDecision.Fail(
                    "queue_quality_not_higher",
                    $"Skipped: queue already has this track at same or higher quality (status={status})."),
                false);
        }

        if (context.RequestedRank <= (existingRank ?? int.MinValue))
        {
            return new QueueDuplicateResolution(null, false);
        }

        if (status is "running")
        {
            return new QueueDuplicateResolution(
                EnqueueItemDecision.Fail(
                    "queue_upgrade_in_progress",
                    "Skipped: matching track is currently downloading. Cancel it first to upgrade quality."),
                false);
        }

        SetPayloadId(payload, existing.QueueUuid);
        var replacementJson = JsonSerializer.Serialize(payload);
        await _queueRepository.UpdateEngineAsync(existing.QueueUuid, context.Identity.Engine, cancellationToken);
        await _queueRepository.UpdateQueueMetadataAsync(
            existing.QueueUuid,
            context.RequestedQualityRank,
            context.Identity.ContentType,
            context.Identity.DestinationFolderId ?? existing.DestinationFolderId,
            cancellationToken);
        await _queueRepository.UpdatePayloadAsync(existing.QueueUuid, replacementJson, cancellationToken);
        await _queueRepository.UpdateStatusAsync(
            existing.QueueUuid,
            "queued",
            error: null,
            downloaded: 0,
            failed: 0,
            progress: 0,
            cancellationToken: cancellationToken);
        _activityLog.Info($"Duplicate upgraded in queue (engine={context.Identity.Engine}): {existing.QueueUuid}");
        return new QueueDuplicateResolution(EnqueueItemDecision.Ok(existing.QueueUuid), false);
    }

    private async Task<EnqueueItemDecision> RequeueFailedDuplicateAsync<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        DownloadQueueItem existing,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        SetPayloadId(payload, existing.QueueUuid);
        var replacementJson = JsonSerializer.Serialize(payload);
        await _queueRepository.UpdateEngineAsync(existing.QueueUuid, context.Identity.Engine, cancellationToken);
        await _queueRepository.UpdateQueueMetadataAsync(
            existing.QueueUuid,
            context.RequestedQualityRank,
            context.Identity.ContentType,
            context.Identity.DestinationFolderId ?? existing.DestinationFolderId,
            cancellationToken);
        await _queueRepository.UpdatePayloadAsync(existing.QueueUuid, replacementJson, cancellationToken);
        await _queueRepository.RequeueAsync(existing.QueueUuid, cancellationToken);
        _activityLog.Info($"Duplicate triggered retry (engine={context.Identity.Engine}): {existing.QueueUuid}");
        _deezspotagListener.Send("updateQueue", new
        {
            uuid = existing.QueueUuid,
            status = "inQueue",
            progress = 0,
            downloaded = 0,
            failed = 0,
            error = default(string)
        });
        return EnqueueItemDecision.Ok(existing.QueueUuid);
    }

    private async Task<EnqueueItemDecision> InsertQueueItemAsync<TPayload>(
        TPayload payload,
        EnqueueItemContext context,
        CancellationToken cancellationToken)
        where TPayload : class
    {
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
            Status: "queued",
            PayloadJson: json,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var insertId = await _queueRepository.EnqueueAsync(item, skipDuplicateCheck: true, cancellationToken: cancellationToken);
        if (!insertId.HasValue)
        {
            return EnqueueItemDecision.Fail("queue_insert_ignored", "Skipped: item was not added to queue because a duplicate already exists.");
        }

        return EnqueueItemDecision.Ok(item.QueueUuid);
    }

    private static DuplicateLookupRequest BuildDuplicateLookupRequest(EnqueueItemContext context)
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
            ArtistName = context.Identity.TrackArtist,
            TrackTitle = context.Identity.TrackTitle,
            DurationMs = context.Identity.DurationMs,
            DestinationFolderId = context.Identity.DestinationFolderId,
            ContentType = context.Identity.ContentType,
            RedownloadCooldownMinutes = context.Settings.RedownloadCooldownMinutes,
            ArtistPrimaryName = context.Identity.TrackPrimaryArtist
        };

    private static MetadataLookupRequest BuildMetadataLookupRequest(EnqueueItemContext context)
        => new()
        {
            ArtistName = context.Identity.TrackArtist,
            TrackTitle = context.Identity.TrackTitle,
            DestinationFolderId = context.Identity.DestinationFolderId,
            ContentType = context.Identity.ContentType,
            ArtistPrimaryName = context.Identity.TrackPrimaryArtist
        };

    private static bool IsCompletedQueueStatus(string status)
        => status is "completed" or "complete";

    private static bool IsRetryableFailedQueueStatus(string status)
        => status is "failed" or "canceled" or "cancelled";

    private static string BuildCooldownMessage(int redownloadCooldownMinutes)
    {
        var cooldownMinutes = Math.Max(0, redownloadCooldownMinutes);
        return cooldownMinutes > 0
            ? $"Skipped: track was downloaded recently and is in cooldown ({cooldownMinutes} minutes)."
            : "Skipped: matching track was downloaded recently.";
    }

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
        payload.DeezerId = intent.DeezerId ?? string.Empty;
        payload.AppleId = intent.AppleId ?? string.Empty;
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

    private async Task<int> EnqueuePrimaryPayloadAsync(
        EngineQueueItemBase payload,
        PrimaryPayloadEnqueueContext context)
    {
        var enqueueDecision = await EnqueueItemAsync(
            payload,
            context.AllowQualityUpgrade,
            context.RequestedQualityRank,
            context.CancellationToken);
        if (enqueueDecision.Success)
        {
            var queueUuid = enqueueDecision.QueueUuid ?? payload.Id;
            context.Queued.Add(queueUuid);
            NotifyQueueAdded(payload);
        }
        else
        {
            RecordSkipReason(context.SkipReasonCodes, context.SkipReasons, enqueueDecision);
        }

        if (context.UseAtmosStereoDual
            && (enqueueDecision.Success || ShouldContinueWithSecondaryAfterPrimarySkip(enqueueDecision)))
        {
            await TryEnqueueAppleAtmosSecondaryAsync(
                new AppleSecondaryEnqueueRequest(
                    context.Intent,
                    context.Settings,
                    context.SecondaryDestinationFolderId,
                    context.AllowQualityUpgrade,
                    context.Queued,
                    context.Availability,
                    context.PreferIsrcOnly,
                    context.CancellationToken));
        }

        return enqueueDecision.Success ? 0 : 1;
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

    private static string? ResolveSecondaryQuality(
        string engine,
        string? primaryQuality,
        bool excludeAtmos)
    {
        return EngineQualityFallback.GetNextLowerQuality(engine, primaryQuality, excludeAtmos);
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

    private static void SetPayloadId<TPayload>(TPayload payload, string queueUuid)
    {
        if (EqualityComparer<TPayload>.Default.Equals(payload, default!) || string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        var payloadObject = (object)payload!;
        var idProperty = payloadObject.GetType().GetProperty("Id");
        if (idProperty is null || !idProperty.CanWrite)
        {
            return;
        }

        idProperty.SetValue(payloadObject, queueUuid);
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
            engine,
            contentType,
            payload.GetType().GetProperty("DurationSeconds")!.GetValue(payload) is int duration && duration > 0 ? duration * 1000 : (int?)null,
            (string)payload.GetType().GetProperty("Title")!.GetValue(payload)!,
            trackArtist,
            NormalizePrimaryArtistForDedupe(trackArtist),
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

    private async Task<bool> IsLibraryDuplicateAsync(LibraryDuplicateCheck check)
    {
        var sourceChecks = new (string Source, string? Value)[]
        {
            ("isrc", check.Isrc),
            (DeezerPlatform, check.DeezerTrackId),
            (SpotifyPlatform, check.SpotifyTrackId),
            (ApplePlatform, check.AppleTrackId)
        };
        var albumChecks = new (string Source, string? AlbumId, string? ArtistId)[]
        {
            (DeezerPlatform, check.DeezerAlbumId, check.DeezerArtistId),
            (SpotifyPlatform, check.SpotifyAlbumId, check.SpotifyArtistId),
            (ApplePlatform, check.AppleAlbumId, check.AppleArtistId)
        };
        return check.DestinationFolderId.HasValue
            ? await ExistsLibraryDuplicateInFolderAsync(
                sourceChecks,
                albumChecks,
                check.TrackTitle,
                check.DestinationFolderId.Value,
                check.RequestedAudioVariant,
                check.CancellationToken)
            : await ExistsLibraryDuplicateGloballyAsync(
                sourceChecks,
                albumChecks,
                check.TrackTitle,
                check.RequestedAudioVariant,
                check.CancellationToken);
    }

    private async Task<bool> ExistsLibraryDuplicateInFolderAsync(
        IEnumerable<(string Source, string? Value)> sourceChecks,
        IEnumerable<(string Source, string? AlbumId, string? ArtistId)> albumChecks,
        string trackTitle,
        long destinationFolderId,
        string? requestedAudioVariant,
        CancellationToken cancellationToken)
    {
        foreach (var (source, value) in sourceChecks)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && await _libraryRepository.ExistsTrackSourceInFolderAsync(
                    source,
                    value,
                    destinationFolderId,
                    audioVariant: requestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        foreach (var (source, albumId, artistId) in albumChecks)
        {
            if (!string.IsNullOrWhiteSpace(albumId)
                && await _libraryRepository.ExistsTrackByAlbumSourceInFolderAsync(
                    source,
                    albumId,
                    trackTitle,
                    artistId,
                    destinationFolderId,
                    audioVariant: requestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ExistsLibraryDuplicateGloballyAsync(
        IEnumerable<(string Source, string? Value)> sourceChecks,
        IEnumerable<(string Source, string? AlbumId, string? ArtistId)> albumChecks,
        string trackTitle,
        string? requestedAudioVariant,
        CancellationToken cancellationToken)
    {
        foreach (var (source, value) in sourceChecks)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && await _libraryRepository.ExistsTrackSourceAsync(
                    source,
                    value,
                    audioVariant: requestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        foreach (var (source, albumId, artistId) in albumChecks)
        {
            if (!string.IsNullOrWhiteSpace(albumId)
                && await _libraryRepository.ExistsTrackByAlbumSourceAsync(
                    source,
                    albumId,
                    trackTitle,
                    artistId,
                    audioVariant: requestedAudioVariant,
                    cancellationToken: cancellationToken))
            {
                return true;
            }
        }

        return false;
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
