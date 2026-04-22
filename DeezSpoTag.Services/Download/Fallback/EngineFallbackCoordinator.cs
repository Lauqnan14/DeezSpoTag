using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Apple;

namespace DeezSpoTag.Services.Download.Fallback;

public sealed class EngineFallbackCoordinator
{
    private const string DeezerEngine = "deezer";
    private const string QobuzEngine = "qobuz";
    private const string AppleEngine = "apple";
    private const string DefaultAppleStorefront = "us";
    private const string DefaultLanguage = "en-US";
    private static readonly string[] AppleFallbackStorefronts = ["us", "gb", "ca", "au"];
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly DeezerIsrcResolver _deezerIsrcResolver;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly IActivityLogWriter _activityLog;
    private sealed record FallbackAdvanceRequest(
        string QueueUuid,
        string CurrentEngine,
        List<string> AutoSources,
        int AutoIndex,
        string SourceUrl,
        string SpotifyId,
        string AppleId,
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
    private sealed record FallbackStepExecutionContext(
        FallbackPayloadMutators Mutators,
        object PayloadForSerialization,
        SourceResolutionRequest ResolutionRequest,
        string? SpotifyId,
        string? ResolvedIsrc);
    private sealed record SourceResolutionRequest(
        string Engine,
        string SourceUrl,
        string SpotifyId,
        string AppleId,
        string? Isrc,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        string DeezerId,
        string Storefront,
        string Language,
        string? MediaUserToken,
        string UserCountry,
        bool FallbackSearchEnabled);

    public EngineFallbackCoordinator(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        SongLinkResolver songLinkResolver,
        DeezerIsrcResolver deezerIsrcResolver,
        AppleMusicCatalogService appleCatalogService,
        IActivityLogWriter activityLog)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _songLinkResolver = songLinkResolver;
        _deezerIsrcResolver = deezerIsrcResolver;
        _appleCatalogService = appleCatalogService;
        _activityLog = activityLog;
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
            AppleId: payload.AppleId,
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
                TrySetDeezerBitrate(payload, step.Source, step.Quality);
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
        mutators.ApplyAutoSources(EncodePlanSteps(planSteps));

        var resolvedIsrc = await ResolveIsrcForFallbackAsync(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            TrySetIsrc(payloadForSerialization, resolvedIsrc);
        }

        var nextIndex = ResolveNextPlanIndex(planSteps, request);
        var userCountry = settings.DeezerCountry;
        var resolvedSpotifyId = await ResolveSpotifyIdForFallbackAsync(request, userCountry, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedSpotifyId))
        {
            TrySetSpotifyId(payloadForSerialization, resolvedSpotifyId);
        }

        var resolutionRequest = BuildSourceResolutionRequest(
            request,
            settings,
            userCountry,
            resolvedSpotifyId,
            resolvedIsrc);
        var stepContext = new FallbackStepExecutionContext(
            mutators,
            payloadForSerialization,
            resolutionRequest,
            resolvedSpotifyId ?? request.SpotifyId,
            resolvedIsrc);

        for (var stepIndex = nextIndex; stepIndex < planSteps.Count; stepIndex++)
        {
            var step = planSteps[stepIndex];
            if (ShouldSkipStep(step, request.CurrentEngine, settings.FallbackBitrate))
            {
                continue;
            }

            var advanced = await TryAdvanceToStepAsync(
                request,
                step,
                stepIndex,
                stepContext,
                cancellationToken);
            if (advanced)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> EncodePlanSteps(List<(string Source, string? Quality)> planSteps)
        => planSteps
            .Select(step => DownloadSourceOrder.EncodeAutoSource(step.Source, step.Quality))
            .ToList();

    private async Task<string?> ResolveIsrcForFallbackAsync(
        FallbackAdvanceRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Isrc))
        {
            return request.Isrc;
        }

        var resolvedIsrc = await _deezerIsrcResolver.ResolveByTrackIdAsync(request.DeezerId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            return resolvedIsrc;
        }

        return await _deezerIsrcResolver.ResolveByMetadataAsync(
            request.Title,
            request.Artist,
            request.Album,
            request.DurationMs,
            cancellationToken);
    }

    private static int ResolveNextPlanIndex(List<(string Source, string? Quality)> planSteps, FallbackAdvanceRequest request)
    {
        var matchedIndex = FindStepIndex(planSteps, request.CurrentEngine, request.Quality);
        // Reconcile persisted auto-index with current engine/quality:
        // some engines can internally step down quality before bubbling a failure.
        // If that happened, prefer the furthest progressed index so fallback does not revisit already-attempted steps.
        var currentIndex = request.AutoIndex >= 0
            ? Math.Max(request.AutoIndex, matchedIndex)
            : matchedIndex;
        return currentIndex + 1;
    }

    private static SourceResolutionRequest BuildSourceResolutionRequest(
        FallbackAdvanceRequest request,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string userCountry,
        string? resolvedSpotifyId,
        string? resolvedIsrc)
    {
        return new SourceResolutionRequest(
            Engine: string.Empty,
            SourceUrl: request.SourceUrl,
            SpotifyId: resolvedSpotifyId ?? request.SpotifyId,
            AppleId: request.AppleId,
            Isrc: resolvedIsrc,
            Title: request.Title,
            Artist: request.Artist,
            Album: request.Album,
            DurationMs: request.DurationMs,
            DeezerId: request.DeezerId,
            Storefront: string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
                ? DefaultAppleStorefront
                : settings.AppleMusic.Storefront,
            Language: string.IsNullOrWhiteSpace(settings.DeezerLanguage)
                ? DefaultLanguage
                : settings.DeezerLanguage,
            MediaUserToken: settings.AppleMusic?.MediaUserToken,
            UserCountry: userCountry,
            FallbackSearchEnabled: settings.FallbackSearch);
    }

    private static bool ShouldSkipStep(
        (string Source, string? Quality) step,
        string currentEngine,
        bool fallbackBitrateEnabled)
    {
        if (string.IsNullOrWhiteSpace(step.Source))
        {
            return true;
        }

        return !fallbackBitrateEnabled
            && string.Equals(step.Source, currentEngine, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryAdvanceToStepAsync(
        FallbackAdvanceRequest request,
        (string Source, string? Quality) step,
        int stepIndex,
        FallbackStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var resolvedUrl = await ResolveSourceUrlAsync(
            context.ResolutionRequest with { Engine = step.Source },
            cancellationToken);
        var canAdvanceWithoutResolvedUrl = CanAdvanceWithoutResolvedUrl(step.Source, context.SpotifyId, request, context.ResolvedIsrc);
        if (string.IsNullOrWhiteSpace(resolvedUrl) && !canAdvanceWithoutResolvedUrl)
        {
            _activityLog.Warn($"Fallback skip: {request.QueueUuid} -> {step.Source} (no resolvable URL)");
            return false;
        }

        context.Mutators.SetSourceUrl(resolvedUrl ?? string.Empty);
        TrySetResolvedAppleId(context.PayloadForSerialization, step.Source, resolvedUrl);
        context.Mutators.ApplyStep((step.Source, step.Quality, stepIndex));
        await PersistAdvancedFallbackStateAsync(request.QueueUuid, step.Source, context.PayloadForSerialization, cancellationToken);
        _activityLog.Info($"Fallback advanced: {request.QueueUuid} -> {step.Source} (auto_index={stepIndex})");
        return true;
    }

    private static void TrySetResolvedAppleId(object payloadForSerialization, string source, string? resolvedUrl)
    {
        if (!string.Equals(source, AppleEngine, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var resolvedAppleId = AppleIdParser.TryExtractFromUrl(resolvedUrl);
        if (!string.IsNullOrWhiteSpace(resolvedAppleId))
        {
            TrySetAppleId(payloadForSerialization, resolvedAppleId);
        }
    }

    private async Task PersistAdvancedFallbackStateAsync(
        string queueUuid,
        string stepSource,
        object payloadForSerialization,
        CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payloadForSerialization);
        await _queueRepository.UpdatePayloadAsync(queueUuid, json, cancellationToken);
        await _queueRepository.UpdateEngineAsync(queueUuid, stepSource, cancellationToken);
        await _queueRepository.UpdateStatusAsync(queueUuid, "queued", error: null, downloaded: 0, failed: 0, progress: 0, cancellationToken: cancellationToken);
    }

    private async Task<string?> ResolveSpotifyIdForFallbackAsync(
        FallbackAdvanceRequest request,
        string userCountry,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return request.SpotifyId;
        }

        if (!string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            var sourceSongLink = await _songLinkResolver.ResolveByUrlAsync(request.SourceUrl, userCountry, cancellationToken);
            if (!string.IsNullOrWhiteSpace(sourceSongLink?.SpotifyId))
            {
                return sourceSongLink.SpotifyId;
            }
        }

        var normalizedDeezerId = NormalizeDeezerTrackId(request.DeezerId);
        if (string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return null;
        }

        var deezerUrl = $"https://www.deezer.com/track/{normalizedDeezerId}";
        var deezerSongLink = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, userCountry, cancellationToken);
        return deezerSongLink?.SpotifyId;
    }

    private static List<(string Source, string? Quality)> BuildPlanSteps(
        List<FallbackPlanStep> fallbackPlan,
        List<string> autoSources,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var steps = new List<(string Source, string? Quality)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (autoSources != null && autoSources.Count > 0)
        {
            foreach (var decoded in autoSources.Select(DownloadSourceOrder.DecodeAutoSource))
            {
                AppendPlanStep(steps, seen, decoded.Source, decoded.Quality);
            }
        }

        if (fallbackPlan != null && fallbackPlan.Count > 0)
        {
            foreach (var step in fallbackPlan)
            {
                AppendPlanStep(steps, seen, step.Engine, step.Quality);
            }
        }

        if (steps.Count == 0)
        {
            foreach (var decoded in DownloadSourceOrder.ResolveAutoSources(settings, includeDeezer: true)
                .Select(DownloadSourceOrder.DecodeAutoSource))
            {
                AppendPlanStep(steps, seen, decoded.Source, decoded.Quality);
            }
        }

        return steps;
    }

    private static void AppendPlanStep(
        List<(string Source, string? Quality)> steps,
        HashSet<string> seen,
        string? source,
        string? quality)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var normalizedSource = source.Trim();
        var normalizedQuality = string.IsNullOrWhiteSpace(quality) ? null : quality.Trim();
        var key = DownloadSourceOrder.EncodeAutoSource(normalizedSource, normalizedQuality);
        if (seen.Add(key))
        {
            steps.Add((normalizedSource, normalizedQuality));
        }
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
        if (string.Equals(request.Engine, DeezerEngine, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return $"https://www.deezer.com/track/{normalizedDeezerId}";
        }

        var appleFallbackUrl = await TryBuildAppleFallbackUrlAsync(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(appleFallbackUrl))
        {
            return appleFallbackUrl;
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

    private async Task<string?> TryBuildAppleFallbackUrlAsync(
        SourceResolutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Engine, AppleEngine, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var appleId = ResolveSeedAppleId(request);
        var resolvedStorefront = await ResolveStorefrontOrDefaultAsync(
            request.Storefront,
            request.MediaUserToken,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(appleId))
        {
            (appleId, resolvedStorefront) = await ResolveAppleIdAcrossCandidatesAsync(
                resolvedStorefront,
                request.Language,
                (_, storefront, language, token) => TryResolveAppleIdByIsrcAsync(
                    request.Isrc,
                    storefront,
                    language,
                    request.MediaUserToken,
                    token),
                request,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(appleId))
        {
            (appleId, resolvedStorefront) = await ResolveAppleIdAcrossCandidatesAsync(
                resolvedStorefront,
                request.Language,
                (sourceRequest, storefront, language, token) => TryResolveAppleIdBySearchAsync(
                    sourceRequest with
                    {
                        Storefront = storefront,
                        Language = language
                    },
                    token),
                request,
                cancellationToken);
        }

        return BuildAppleMediaUrl(appleId, resolvedStorefront);
    }

    private static string? ResolveSeedAppleId(SourceResolutionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AppleId))
        {
            return request.AppleId;
        }

        return IsServiceUrlMatch(request.SourceUrl, AppleEngine)
            ? AppleIdParser.TryExtractFromUrl(request.SourceUrl)
            : null;
    }

    private async Task<string> ResolveStorefrontOrDefaultAsync(
        string storefront,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        var resolvedStorefront = await _appleCatalogService.ResolveStorefrontAsync(
            storefront,
            mediaUserToken,
            cancellationToken);
        return string.IsNullOrWhiteSpace(resolvedStorefront)
            ? DefaultAppleStorefront
            : resolvedStorefront;
    }

    private static async Task<(string? AppleId, string Storefront)> ResolveAppleIdAcrossCandidatesAsync(
        string primaryStorefront,
        string language,
        Func<SourceResolutionRequest, string, string, CancellationToken, Task<string?>> resolver,
        SourceResolutionRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var storefrontCandidate in BuildStorefrontCandidates(primaryStorefront))
        {
            foreach (var languageCandidate in BuildLanguageCandidates(language))
            {
                var resolvedAppleId = await resolver(request, storefrontCandidate, languageCandidate, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolvedAppleId))
                {
                    return (resolvedAppleId, storefrontCandidate);
                }
            }
        }

        return (null, primaryStorefront);
    }

    private static string? BuildAppleMediaUrl(string? appleId, string storefront)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return null;
        }

        return appleId.StartsWith("ra.", StringComparison.OrdinalIgnoreCase)
            ? $"https://music.apple.com/{storefront}/station/{appleId}"
            : $"https://music.apple.com/{storefront}/song/{appleId}?i={appleId}";
    }

    private async Task<string?> TryResolveAppleIdByIsrcAsync(
        string? isrc,
        string storefront,
        string language,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            using var doc = await _appleCatalogService.GetSongByIsrcAsync(
                isrc,
                storefront,
                language,
                cancellationToken,
                mediaUserToken);
            return TryExtractAppleIdFromCatalog(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryResolveAppleIdBySearchAsync(
        SourceResolutionRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var term in BuildAppleSearchTerms(request.Title, request.Artist, request.Album))
        {
            try
            {
                using var doc = await _appleCatalogService.SearchAsync(
                    term,
                    limit: 10,
                    storefront: request.Storefront,
                    language: request.Language,
                    cancellationToken,
                    new AppleMusicCatalogService.AppleSearchOptions(
                        TypesOverride: "songs",
                        IncludeRelationshipsTracks: false));
                var id = TryExtractAppleSongIdFromSearch(doc.RootElement, request);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }
            catch
            {
                // Ignore and continue with other terms.
            }
        }

        return null;
    }

    private static string? TryExtractAppleIdFromCatalog(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != System.Text.Json.JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return null;
        }

        var first = data[0];
        return first.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private static string? TryExtractAppleSongIdFromSearch(System.Text.Json.JsonElement root, SourceResolutionRequest request)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != System.Text.Json.JsonValueKind.Object
            || !results.TryGetProperty("songs", out var songs)
            || songs.ValueKind != System.Text.Json.JsonValueKind.Object
            || !songs.TryGetProperty("data", out var data)
            || data.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var bestScore = int.MinValue;
        string? bestId = null;
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var score = ScoreCandidate(item, request);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        if (bestScore >= 65)
        {
            return bestId;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    private static int ScoreCandidate(System.Text.Json.JsonElement item, SourceResolutionRequest request)
    {
        if (!item.TryGetProperty("attributes", out var attrs)
            || attrs.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return 0;
        }

        var title = TryReadString(attrs, "name");
        var artist = TryReadString(attrs, "artistName");
        var album = TryReadString(attrs, "albumName");
        var isrc = TryReadString(attrs, "isrc");
        var durationInMillis = attrs.TryGetProperty("durationInMillis", out var durationProp)
            && durationProp.TryGetInt32(out var parsedDuration)
            ? parsedDuration
            : 0;

        var score = 0;
        score += ScoreTextMatch(request.Title, title, 60);
        score += ScoreTextMatch(request.Artist, artist, 30);
        score += ScoreTextMatch(request.Album, album, 15);
        if (!string.IsNullOrWhiteSpace(request.Isrc)
            && !string.IsNullOrWhiteSpace(isrc)
            && string.Equals(request.Isrc.Trim(), isrc.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (request.DurationMs.HasValue && request.DurationMs.Value > 0 && durationInMillis > 0)
        {
            var diff = Math.Abs(request.DurationMs.Value - durationInMillis);
            if (diff <= 2000)
            {
                score += 10;
            }
            else if (diff <= 5000)
            {
                score += 6;
            }
            else if (diff <= 10000)
            {
                score += 3;
            }
        }

        return score;
    }

    private static List<string> BuildAppleSearchTerms(string? title, string? artist, string? album)
    {
        var terms = new List<string>();
        var normalizedTitle = title?.Trim();
        var normalizedArtist = artist?.Trim();
        var normalizedAlbum = album?.Trim();
        var cleanedTitle = NormalizeForCompare(normalizedTitle ?? string.Empty);
        var cleanedArtist = NormalizeForCompare(normalizedArtist ?? string.Empty);
        var cleanedAlbum = NormalizeForCompare(normalizedAlbum ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(normalizedTitle) && !string.IsNullOrWhiteSpace(normalizedArtist))
        {
            terms.Add($"{normalizedTitle} {normalizedArtist}");
        }

        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            terms.Add(normalizedTitle);
        }

        if (!string.IsNullOrWhiteSpace(normalizedTitle) && !string.IsNullOrWhiteSpace(normalizedAlbum))
        {
            terms.Add($"{normalizedTitle} {normalizedAlbum}");
        }

        if (!string.IsNullOrWhiteSpace(cleanedTitle) && !string.IsNullOrWhiteSpace(cleanedArtist))
        {
            terms.Add($"{cleanedTitle} {cleanedArtist}");
        }

        if (!string.IsNullOrWhiteSpace(cleanedTitle))
        {
            terms.Add(cleanedTitle);
        }

        if (!string.IsNullOrWhiteSpace(cleanedTitle) && !string.IsNullOrWhiteSpace(cleanedAlbum))
        {
            terms.Add($"{cleanedTitle} {cleanedAlbum}");
        }

        return terms
            .Select(term => term.Trim())
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildStorefrontCandidates(string primary)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary))
        {
            candidates.Add(primary.Trim());
        }

        candidates.AddRange(AppleFallbackStorefronts);
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildLanguageCandidates(string? language)
    {
        var baseLanguage = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
        var values = new List<string> { baseLanguage };
        var dashIndex = baseLanguage.IndexOf('-');
        if (dashIndex > 0)
        {
            values.Add(baseLanguage[..dashIndex]);
        }

        if (!baseLanguage.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            values.Add(DefaultLanguage);
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ScoreTextMatch(string? expected, string? actual, int maxScore)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return 0;
        }

        var normalizedExpected = NormalizeForCompare(expected);
        var normalizedActual = NormalizeForCompare(actual);
        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedActual))
        {
            return 0;
        }

        if (string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase))
        {
            return maxScore;
        }

        var expectedTokens = normalizedExpected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var actualTokens = normalizedActual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (expectedTokens.Length == 0 || actualTokens.Length == 0)
        {
            return 0;
        }

        var overlap = expectedTokens.Intersect(actualTokens, StringComparer.OrdinalIgnoreCase).Count();
        var denominator = Math.Max(expectedTokens.Length, actualTokens.Length);
        if (denominator <= 0 || overlap <= 0)
        {
            return 0;
        }

        return (int)Math.Round((double)overlap / denominator * maxScore);
    }

    private static string NormalizeForCompare(string value)
    {
        return new string(value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ')
            .ToArray())
            .Trim();
    }

    private static string? TryReadString(System.Text.Json.JsonElement node, string propertyName)
    {
        return node.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
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
            DeezerEngine => songLink.DeezerUrl,
            _ => null
        };
    }

    private static bool IsServiceUrlMatch(string url, string engine)
    {
        return engine switch
        {
            DeezerEngine => url.Contains("deezer.com", StringComparison.OrdinalIgnoreCase),
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
        string? spotifyId,
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
            return !string.IsNullOrWhiteSpace(spotifyId);
        }

        if (string.Equals(engine, "tidal", StringComparison.OrdinalIgnoreCase))
        {
            // Tidal path can resolve from Spotify ID or from metadata in-engine.
            return !string.IsNullOrWhiteSpace(spotifyId)
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

    private static void TrySetSpotifyId(object payload, string spotifyId)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return;
        }

        var property = payload.GetType().GetProperty("SpotifyId");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(payload, spotifyId);
    }

    private static void TrySetAppleId(object payload, string appleId)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return;
        }

        var property = payload.GetType().GetProperty("AppleId");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(payload, appleId);
    }

    private static void TrySetDeezerBitrate(object payload, string source, string? quality)
    {
        if (!string.Equals(source, DeezerEngine, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(quality)
            || !int.TryParse(quality, out var bitrate)
            || bitrate <= 0)
        {
            return;
        }

        var property = payload.GetType().GetProperty("Bitrate");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        if (property.PropertyType == typeof(int))
        {
            property.SetValue(payload, bitrate);
            return;
        }

        if (property.PropertyType == typeof(int?))
        {
            property.SetValue(payload, (int?)bitrate);
        }
    }
}
