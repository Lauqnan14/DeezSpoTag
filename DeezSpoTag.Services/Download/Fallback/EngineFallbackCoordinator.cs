using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Fallback;

public sealed class EngineFallbackCoordinator
{
    private const string QobuzEngine = "qobuz";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly DeezerIsrcResolver _deezerIsrcResolver;
    private readonly IActivityLogWriter _activityLog;
    private sealed record FallbackAdvanceRequest(
        string QueueUuid,
        string CurrentEngine,
        List<string> AutoSources,
        int AutoIndex,
        string SourceUrl,
        string SpotifyId,
        string Isrc,
        string DeezerId,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        string Quality,
        List<FallbackPlanStep> FallbackPlan);
    private sealed record FallbackPayloadMutators(
        Action<(string Source, string? Quality, int Index)> ApplyStep,
        Action<List<string>> ApplyAutoSources,
        Action<string> SetSourceUrl);
    private sealed record SourceResolutionRequest(
        string Engine,
        string SourceUrl,
        string SpotifyId,
        string? Isrc,
        string DeezerId,
        string UserCountry,
        bool FallbackSearchEnabled);

    public EngineFallbackCoordinator(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        SongLinkResolver songLinkResolver,
        DeezerIsrcResolver deezerIsrcResolver,
        IActivityLogWriter activityLog,
        ILogger<EngineFallbackCoordinator> logger,
        DeezSpoTagApp deezerApp)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _songLinkResolver = songLinkResolver;
        _deezerIsrcResolver = deezerIsrcResolver;
        _activityLog = activityLog;
        _ = logger;
        _ = deezerApp;
    }

    public Task<bool> TryAdvanceAsync<TPayload>(
        string queueUuid,
        string currentEngine,
        TPayload payload,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var request = new FallbackAdvanceRequest(
            QueueUuid: queueUuid,
            CurrentEngine: currentEngine,
            AutoSources: payload.AutoSources,
            AutoIndex: payload.AutoIndex,
            SourceUrl: payload.SourceUrl,
            SpotifyId: payload.SpotifyId,
            Isrc: payload.Isrc,
            DeezerId: payload.DeezerId,
            Title: payload.Title,
            Artist: payload.Artist,
            Album: payload.Album,
            DurationMs: payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null,
            Quality: payload.Quality,
            FallbackPlan: payload.FallbackPlan);

        var mutators = new FallbackPayloadMutators(
            ApplyStep: step =>
            {
                payload.Engine = step.Source;
                payload.SourceService = step.Source;
                payload.Quality = step.Quality ?? payload.Quality;
                payload.AutoIndex = step.Index;
            },
            ApplyAutoSources: sources => payload.AutoSources = sources,
            SetSourceUrl: url => payload.SourceUrl = url);

        return TryAdvanceCoreAsync(
            request,
            mutators,
            payload,
            cancellationToken);
    }

    private async Task<bool> TryAdvanceCoreAsync(
        FallbackAdvanceRequest request,
        FallbackPayloadMutators mutators,
        object payloadForSerialization,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var planSteps = BuildPlanSteps(request.FallbackPlan, request.AutoSources, settings);
        mutators.ApplyAutoSources(planSteps.Select(step => DownloadSourceOrder.EncodeAutoSource(step.Source, step.Quality)).ToList());

        var resolvedIsrc = request.Isrc;
        if (string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            resolvedIsrc = await _deezerIsrcResolver.ResolveByTrackIdAsync(request.DeezerId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            resolvedIsrc = await _deezerIsrcResolver.ResolveByMetadataAsync(
                request.Title,
                request.Artist,
                request.Album,
                request.DurationMs,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            TrySetIsrc(payloadForSerialization, resolvedIsrc);
        }

        var matchedIndex = FindStepIndex(planSteps, request.CurrentEngine, request.Quality);
        // Reconcile persisted auto-index with current engine/quality:
        // some engines can internally step down quality before bubbling a failure.
        // If that happened, prefer the furthest progressed index so fallback does not revisit already-attempted steps.
        var currentIndex = request.AutoIndex >= 0
            ? Math.Max(request.AutoIndex, matchedIndex)
            : matchedIndex;
        var nextIndex = currentIndex + 1;
        var userCountry = settings.DeezerCountry;
        var resolutionRequest = new SourceResolutionRequest(
            Engine: string.Empty,
            SourceUrl: request.SourceUrl,
            SpotifyId: request.SpotifyId,
            Isrc: resolvedIsrc,
            DeezerId: request.DeezerId,
            UserCountry: userCountry,
            FallbackSearchEnabled: settings.FallbackSearch);

        while (nextIndex < planSteps.Count)
        {
            var step = planSteps[nextIndex];
            if (string.IsNullOrWhiteSpace(step.Source))
            {
                nextIndex++;
                continue;
            }

            if (!settings.FallbackBitrate
                && string.Equals(step.Source, request.CurrentEngine, StringComparison.OrdinalIgnoreCase))
            {
                nextIndex++;
                continue;
            }

            var resolvedUrl = await ResolveSourceUrlAsync(
                resolutionRequest with { Engine = step.Source },
                cancellationToken);
            var canAdvanceWithoutResolvedUrl = CanAdvanceWithoutResolvedUrl(step.Source, request, resolvedIsrc);
            if (string.IsNullOrWhiteSpace(resolvedUrl) && !canAdvanceWithoutResolvedUrl)
            {
                _activityLog.Warn($"Fallback skip: {request.QueueUuid} -> {step.Source} (no resolvable URL)");
                nextIndex++;
                continue;
            }

            mutators.SetSourceUrl(resolvedUrl ?? string.Empty);

            mutators.ApplyStep((step.Source, step.Quality, nextIndex));
            var json = System.Text.Json.JsonSerializer.Serialize(payloadForSerialization);
            await _queueRepository.UpdatePayloadAsync(request.QueueUuid, json, cancellationToken);
            await _queueRepository.UpdateEngineAsync(request.QueueUuid, step.Source, cancellationToken);
            await _queueRepository.UpdateStatusAsync(request.QueueUuid, "queued", error: null, downloaded: 0, failed: 0, progress: 0, cancellationToken: cancellationToken);
            _activityLog.Info($"Fallback advanced: {request.QueueUuid} -> {step.Source} (auto_index={nextIndex})");
            return true;
        }

        return false;
    }

    private static List<(string Source, string? Quality)> BuildPlanSteps(
        List<FallbackPlanStep> fallbackPlan,
        List<string> autoSources,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        if (fallbackPlan != null && fallbackPlan.Count > 0)
        {
            return fallbackPlan
                .Where(step => !string.IsNullOrWhiteSpace(step.Engine))
                .Select(step => (step.Engine, step.Quality))
                .ToList();
        }

        var forcedService = settings.Service?.Trim().ToLowerInvariant();
        var isAuto = string.Equals(forcedService, "auto", StringComparison.OrdinalIgnoreCase);

        if (autoSources != null && autoSources.Count > 0)
        {
            var decoded = autoSources
                .Select(DownloadSourceOrder.DecodeAutoSource)
                .Select(step => (step.Source, step.Quality))
                .ToList();

            if (!isAuto && !string.IsNullOrWhiteSpace(forcedService))
            {
                var filtered = decoded
                    .Where(step => string.Equals(step.Source, forcedService, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (filtered.Count > 0)
                {
                    return filtered;
                }
            }

            return decoded;
        }

        autoSources = DownloadSourceOrder.ResolveAutoSources(settings, includeDeezer: true);

        return autoSources
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .Select(step => (step.Source, step.Quality))
            .ToList();
    }

    private static int FindStepIndex(List<(string Source, string? Quality)> autoSources, string engine, string quality)
    {
        for (var i = 0; i < autoSources.Count; i++)
        {
            var step = autoSources[i];
            if (string.Equals(step.Source, engine, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(step.Quality) || string.IsNullOrWhiteSpace(quality))
                {
                    return i;
                }

                if (string.Equals(step.Quality, quality, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private async Task<string?> ResolveSourceUrlAsync(
        SourceResolutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SourceUrl) && IsServiceUrlMatch(request.SourceUrl, request.Engine))
        {
            return request.SourceUrl;
        }

        var normalizedDeezerId = NormalizeDeezerTrackId(request.DeezerId);
        if (string.Equals(request.Engine, "deezer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return $"https://www.deezer.com/track/{normalizedDeezerId}";
        }

        var songLink = await ResolveSongLinkFromDeezerAsync(normalizedDeezerId, request.UserCountry, cancellationToken);
        var resolvedUrl = GetEngineUrl(songLink, request.Engine);

        (resolvedUrl, songLink) = await TryResolveFromSourceUrlAsync(
            request,
            songLink,
            resolvedUrl,
            cancellationToken);
        (resolvedUrl, songLink) = await TryResolveFromSpotifyAsync(
            request,
            songLink,
            resolvedUrl,
            cancellationToken);
        (resolvedUrl, songLink) = await TryResolveFromSpotifyFallbackSearchAsync(
            request,
            songLink,
            resolvedUrl,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return resolvedUrl;
        }

        if (string.Equals(request.Engine, QobuzEngine, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.Isrc))
        {
            var qobuzUrl = await _songLinkResolver.ResolveQobuzUrlByIsrcAsync(request.Isrc, cancellationToken);
            if (!string.IsNullOrWhiteSpace(qobuzUrl))
            {
                return qobuzUrl;
            }
        }

        return resolvedUrl;
    }

    private async Task<SongLinkResult?> ResolveSongLinkFromDeezerAsync(
        string? normalizedDeezerId,
        string userCountry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return null;
        }

        var deezerUrl = $"https://www.deezer.com/track/{normalizedDeezerId}";
        return await _songLinkResolver.ResolveByUrlAsync(deezerUrl, userCountry, cancellationToken);
    }

    private async Task<(string? ResolvedUrl, SongLinkResult? SongLink)> TryResolveFromSourceUrlAsync(
        SourceResolutionRequest request,
        SongLinkResult? currentSongLink,
        string? currentResolvedUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(currentResolvedUrl) || string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            return (currentResolvedUrl, currentSongLink);
        }

        var sourceUrlSongLink = await _songLinkResolver.ResolveByUrlAsync(request.SourceUrl, request.UserCountry, cancellationToken);
        return PreferSongLinkCandidate(request.Engine, currentSongLink, sourceUrlSongLink, currentResolvedUrl);
    }

    private async Task<(string? ResolvedUrl, SongLinkResult? SongLink)> TryResolveFromSpotifyAsync(
        SourceResolutionRequest request,
        SongLinkResult? currentSongLink,
        string? currentResolvedUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(currentResolvedUrl) || string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return (currentResolvedUrl, currentSongLink);
        }

        var spotifySongLink = await _songLinkResolver.ResolveSpotifyTrackAsync(request.SpotifyId, cancellationToken);
        return PreferSongLinkCandidate(request.Engine, currentSongLink, spotifySongLink, currentResolvedUrl);
    }

    private async Task<(string? ResolvedUrl, SongLinkResult? SongLink)> TryResolveFromSpotifyFallbackSearchAsync(
        SourceResolutionRequest request,
        SongLinkResult? currentSongLink,
        string? currentResolvedUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(currentResolvedUrl)
            || !request.FallbackSearchEnabled
            || string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return (currentResolvedUrl, currentSongLink);
        }

        var resolvedDeezerId = await _songLinkResolver.ResolveDeezerIdFromSpotifyAsync(request.SpotifyId, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedDeezerId))
        {
            return (currentResolvedUrl, currentSongLink);
        }

        var deezerUrl = $"https://www.deezer.com/track/{resolvedDeezerId}";
        var resolvedDeezerSongLink = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, request.UserCountry, cancellationToken);
        return PreferSongLinkCandidate(request.Engine, currentSongLink, resolvedDeezerSongLink, currentResolvedUrl);
    }

    private static (string? ResolvedUrl, SongLinkResult? SongLink) PreferSongLinkCandidate(
        string engine,
        SongLinkResult? currentSongLink,
        SongLinkResult? candidateSongLink,
        string? currentResolvedUrl)
    {
        var candidateUrl = GetEngineUrl(candidateSongLink, engine);
        if (!string.IsNullOrWhiteSpace(candidateUrl) || currentSongLink == null)
        {
            return (candidateUrl, candidateSongLink);
        }

        return (currentResolvedUrl, currentSongLink);
    }

    private static string? GetEngineUrl(SongLinkResult? songLink, string engine)
    {
        if (songLink == null)
        {
            return null;
        }

        return engine switch
        {
            "apple" => songLink.AppleMusicUrl,
            "tidal" => songLink.TidalUrl,
            "amazon" => songLink.AmazonUrl,
            QobuzEngine => songLink.QobuzUrl,
            "deezer" => songLink.DeezerUrl,
            _ => null
        };
    }

    private static bool IsServiceUrlMatch(string url, string engine)
    {
        return engine switch
        {
            "deezer" => url.Contains("deezer.com", StringComparison.OrdinalIgnoreCase),
            "apple" => url.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase),
            "tidal" => url.Contains("tidal.com", StringComparison.OrdinalIgnoreCase),
            "amazon" => url.Contains("amazon.", StringComparison.OrdinalIgnoreCase)
                        || url.Contains("music.amazon", StringComparison.OrdinalIgnoreCase),
            QobuzEngine => url.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool CanAdvanceWithoutResolvedUrl(
        string engine,
        FallbackAdvanceRequest request,
        string? resolvedIsrc)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return false;
        }

        if (string.Equals(engine, QobuzEngine, StringComparison.OrdinalIgnoreCase))
        {
            // Qobuz path can proceed with ISRC-only resolution.
            return !string.IsNullOrWhiteSpace(resolvedIsrc);
        }

        if (string.Equals(engine, "amazon", StringComparison.OrdinalIgnoreCase))
        {
            // Amazon path can resolve from Spotify ID when URL is missing.
            return !string.IsNullOrWhiteSpace(request.SpotifyId);
        }

        if (string.Equals(engine, "tidal", StringComparison.OrdinalIgnoreCase))
        {
            // Tidal path can resolve from Spotify ID or from metadata in-engine.
            return !string.IsNullOrWhiteSpace(request.SpotifyId)
                || !string.IsNullOrWhiteSpace(resolvedIsrc)
                || (!string.IsNullOrWhiteSpace(request.Title) && !string.IsNullOrWhiteSpace(request.Artist));
        }

        return false;
    }

    private static string? NormalizeDeezerTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value, out _) ? value : null;
    }

    private static void TrySetIsrc(object payload, string isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return;
        }

        var property = payload.GetType().GetProperty("Isrc");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(payload, isrc);
    }
}
