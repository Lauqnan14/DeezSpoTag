using System.Text.Json;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Web.Services.Audiomack;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistMetadataCacheRefreshService
{
    /// <summary>
    ///     The engine ids below are aliased to the one canonical definition.
    /// </summary>
    /// <remarks>
    ///     Each one is either the key an artist source id is stored and read back under, or the
    ///     label a biography provider maps to. A copy that drifted from the key the rows were
    ///     written with would read back no id and skip that engine's refresh entirely. The names
    ///     are kept local because they read better at the dispatch sites; the value is defined once.
    /// </remarks>
    private const string SpotifySource = DownloadTagSourceHelper.SpotifySource;

    private const string AppleSource = DownloadTagSourceHelper.AppleSource;

    private const string TidalSource = DownloadTagSourceHelper.TidalSource;

    private const string QobuzSource = DownloadTagSourceHelper.QobuzSource;

    private enum BiographyProvider
    {
        Deezer,
        Spotify,
        Apple,
        Tidal,
        Qobuz,
        LastFm,
        Audiomack
    }

    private static readonly BiographyProvider[] BiographyProviders =
    [
        BiographyProvider.Spotify,
        BiographyProvider.Apple,
        BiographyProvider.Tidal,
        BiographyProvider.LastFm,
        BiographyProvider.Audiomack,
        BiographyProvider.Qobuz
    ];
    private static readonly TimeSpan ArtistYield = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan ArtistRefreshTimeout = TimeSpan.FromMinutes(10);
    private readonly LibraryRepository _repository;
    private readonly ArtistArtworkCatalogService _artworkCatalog;
    private readonly SpotifyArtistService _spotify;
    private readonly AppleArtistBiographyService _apple;
    private readonly ITidalAccessTokenProvider _tidalTokens;
    private readonly QobuzArtistService _qobuz;
    private readonly LastFmArtistImageService _lastFm;
    private readonly AudiomackArtistLocationService _audiomack;
    private readonly ArtistMetadataUpdaterService _visualSlots;
    private readonly ArtistMediaExtrasCacheService _mediaExtras;
    private readonly UserPreferencesStore _preferences;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<ArtistMetadataCacheRefreshService> _logger;
    private readonly ArtistAliasService? _artistAliasService;
    private readonly DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResolver? _locationResolver;
    private readonly ArtistPageCacheRepository? _artistPageCache;
    private readonly ArtistMetadataProviderGate _providerGate;
    private readonly LibraryConfigStore? _configStore;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> _artistLocks = new();
    public const string ManifestSource = "artist-refresh";

    public ArtistMetadataCacheRefreshService(
        LibraryRepository repository,
        ArtistArtworkCatalogService artworkCatalog,
        SpotifyArtistService spotify,
        AppleArtistBiographyService apple,
        ITidalAccessTokenProvider tidalTokens,
        QobuzArtistService qobuz,
        LastFmArtistImageService lastFm,
        AudiomackArtistLocationService audiomack,
        ArtistMetadataUpdaterService visualSlots,
        ArtistMediaExtrasCacheService mediaExtras,
        UserPreferencesStore preferences,
        IHttpClientFactory httpClients,
        ILogger<ArtistMetadataCacheRefreshService> logger,
        ArtistAliasService? artistAliasService = null,
        DeezSpoTag.Web.Services.ArtistLocation.ArtistLocationResolver? locationResolver = null,
        ArtistPageCacheRepository? artistPageCache = null,
        ArtistMetadataProviderGate? providerGate = null, LibraryConfigStore? configStore = null)
    {
        _configStore = configStore;
        _repository = repository;
        _artworkCatalog = artworkCatalog;
        _spotify = spotify;
        _apple = apple;
        _tidalTokens = tidalTokens;
        _qobuz = qobuz;
        _lastFm = lastFm;
        _audiomack = audiomack;
        _visualSlots = visualSlots;
        _mediaExtras = mediaExtras;
        _preferences = preferences;
        _httpClients = httpClients;
        _logger = logger;
        _providerGate = providerGate ?? new ArtistMetadataProviderGate(_logger);
        _artistAliasService = artistAliasService;
        _locationResolver = locationResolver;
        _artistPageCache = artistPageCache;
    }

    public async Task<ArtistMetadataCacheRefreshResult> RefreshAsync(
        ArtistMetadataCacheRefreshRequest request,
        IProgress<ArtistMetadataOperationProgress>? progress,
        CancellationToken cancellationToken)
        => await RefreshAsync(request, progress, resumedRun: null, outcomeSink: null, cancellationToken);

    public async Task<ArtistMetadataCacheRefreshResult> RefreshAsync(
        ArtistMetadataCacheRefreshRequest request,
        IProgress<ArtistMetadataOperationProgress>? progress,
        ArtistRunResumeContext? resumedRun,
        Action<ArtistRunOutcomeRecord>? outcomeSink,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<long>, Task>? targetSink = null)
    {
        if (!_repository.IsConfigured)
        {
            return new ArtistMetadataCacheRefreshResult(0, 0, 1, "Library database is not configured.");
        }

        var artists = SelectRunArtists(
            await _repository.GetArtistsAsync("all", request.FolderId, cancellationToken), request, resumedRun);
        var targetArtistIds = resumedRun is { TargetArtistIds.Count: > 0 }
            ? resumedRun.TargetArtistIds : artists.Select(artist => artist.Id).ToList();
        var total = targetArtistIds.Count;
        if (targetSink is not null) await targetSink(targetArtistIds);

        // Carried-over outcomes seed the tallies so a resumed run reports the same totals as a run
        // that was never interrupted, instead of counting only the post-resume slice against the
        // full artist list.
        // Legacy artist-level success is not evidence that requested cache stages completed.
        var priorOutcomes = resumedRun?.Outcomes?.Where(outcome => outcome.CacheWork is { Count: > 0 }).ToArray();
        var prior = CountOutcomes(priorOutcomes);
        var priorFailures = (priorOutcomes ?? Array.Empty<ArtistRunOutcomeRecord>())
            .Where(outcome => outcome.Outcome == ArtistRunOutcomes.Failed).Select(outcome => outcome.ArtistId).ToHashSet();
        var completed = priorOutcomes is { Length: > 0 }
            ? ArtistRunOutcomes.BuildResumeSkipSet(priorOutcomes!)
            : new HashSet<long>();
        var processed = completed.Intersect(targetArtistIds).Count();
        var succeeded = prior.Succeeded;
        var failed = prior.Failed;
        var gate = _providerGate;
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Contains(artist.Id))
            {
                continue;
            }

            if (priorFailures.Remove(artist.Id)) failed--;
            processed++;
            progress?.Report(new ArtistMetadataOperationProgress(
                processed, total, artist.Name, null, succeeded, failed));
            try
            {
                var refreshed = await RefreshArtistWithOutcomeAsync(request with
                {
                    ArtistId = artist.Id,
                    ForceProviderRefresh = request.ForceProviderRefresh && resumedRun is null
                }, cancellationToken);
                if (refreshed.IsComplete) succeeded++;
                else failed++;
                outcomeSink?.Invoke(new ArtistRunOutcomeRecord(artist.Id,
                    refreshed.IsComplete ? ArtistRunOutcomes.Succeeded : ArtistRunOutcomes.Failed,
                    CacheWork: refreshed.Work));
                progress?.Report(new ArtistMetadataOperationProgress(
                    processed, total, artist.Name, artist.Id, succeeded, failed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failed++;
                outcomeSink?.Invoke(new ArtistRunOutcomeRecord(
                    artist.Id, ArtistRunOutcomes.Failed, ArtistRunSkipReasons.Timeout));
                progress?.Report(new ArtistMetadataOperationProgress(
                    processed, total, artist.Name, artist.Id, succeeded, failed));
                _logger.LogWarning(
                    "Artist metadata cache refresh timed out for artist {ArtistId} ({ArtistName}) after {TimeoutMinutes} minutes.",
                    artist.Id,
                    artist.Name,
                    ArtistRefreshTimeout.TotalMinutes);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                outcomeSink?.Invoke(new ArtistRunOutcomeRecord(artist.Id, ArtistRunOutcomes.Failed));
                progress?.Report(new ArtistMetadataOperationProgress(
                    processed, total, artist.Name, artist.Id, succeeded, failed));
                _logger.LogWarning(ex, "Artist metadata cache refresh failed for artist {ArtistId}.", artist.Id);
            }

            await Task.Delay(ArtistYield, cancellationToken);
        }

        return new ArtistMetadataCacheRefreshResult(total, succeeded, failed, failed > 0 ? "Some artist cache work remains unresolved." : null);
    }

    private async Task<bool> RetainArtistEnrichmentAsync(long artistId, string artistName, string? source, CancellationToken cancellationToken)
    {
        if (_locationResolver is null || _artistPageCache is null)
        {
            return false;
        }

        var location = await _locationResolver.ResolveRequiredAsync(artistId, artistName, source, _providerGate, cancellationToken).ConfigureAwait(false);
        if (location is null)
        {
            return false;
        }

        IReadOnlyList<DeezSpoTag.Core.Models.LanguageMetadataEvidence> languages = [];
        var retainedEntry = await _artistPageCache.TryGetRequiredAsync("artist-enrichment",
            artistId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        if (retainedEntry is not null)
        {
            try
            {
                using var payload = JsonDocument.Parse(retainedEntry.PayloadJson);
                if (payload.RootElement.GetProperty("version").GetString() == "v1")
                {
                    var retained = payload.RootElement.GetProperty("metadata").Deserialize<DeezSpoTag.Core.Models.ArtistEnrichmentMetadata>();
                    if (retained?.ArtistId == artistId)
                        languages = retained.Languages.Where(e => e.Scope == DeezSpoTag.Core.Models.LanguageMetadataScope.Artist).ToArray();
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { _logger.LogDebug(ex, "Retained artist language payload is unavailable for {ArtistId}.", artistId); }
        }

        var metadata = new DeezSpoTag.Core.Models.ArtistEnrichmentMetadata(
            artistId,
            location.ArtistName ?? artistName,
            location.ArtistRole,
            location.Source,
            location.SourceReference ?? string.Empty,
            null,
            new DeezSpoTag.Core.Models.ArtistEnrichmentLocation(
                location.Country, location.City, location.Region, location.CountryCode,
                location.Source, location.SourceReference, location.RetrievedAt,
                location.KnownAt, location.ResolutionMethod, location.LocationMeaning) { Hometown = location.Hometown },
            languages);
        await _artistPageCache.UpsertRequiredAsync(
            "artist-enrichment",
            artistId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new { version = "v1", metadata, retainedAt = DateTimeOffset.UtcNow }),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static List<ArtistDto> SelectRunArtists(IEnumerable<ArtistDto> artists,
        ArtistMetadataCacheRefreshRequest request, ArtistRunResumeContext? resumedRun)
    {
        var frozen = resumedRun is { TargetArtistIds.Count: > 0 }
            ? resumedRun.TargetArtistIds.ToHashSet() : null;
        return artists.Where(artist => artist.Id > 0 && !string.IsNullOrWhiteSpace(artist.Name))
            .Where(artist => !request.ArtistId.HasValue || artist.Id == request.ArtistId.Value)
            .Where(artist => frozen is null || frozen.Contains(artist.Id))
            .ToList();
    }

    private static (int Succeeded, int Failed) CountOutcomes(IReadOnlyList<ArtistRunOutcomeRecord>? outcomes)
    {
        if (outcomes is null)
        {
            return (0, 0);
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.Outcome == ArtistRunOutcomes.Succeeded || outcome.Outcome == ArtistRunOutcomes.Partial)
            {
                succeeded++;
            }
            else if (outcome.Outcome == ArtistRunOutcomes.Failed)
            {
                failed++;
            }
        }

        return (succeeded, failed);
    }

    public Task<bool> RefreshArtistAsync(
        long artistId,
        string artistName,
        string? source,
        bool includePopularSongs,
        CancellationToken cancellationToken)
        => RefreshArtistAsync(artistId, artistName, source, includePopularSongs, includeDiscography: false, cancellationToken, providerGate: null);

    public Task<bool> RefreshArtistAsync(
        long artistId,
        string artistName,
        string? source,
        bool includePopularSongs,
        bool includeDiscography,
        CancellationToken cancellationToken)
        => RefreshArtistAsync(artistId, artistName, source, includePopularSongs, includeDiscography, cancellationToken, providerGate: null);

    public async Task<bool> RefreshArtistAsync(
        long artistId, string artistName, string? source, bool includePopularSongs, bool includeDiscography,
        CancellationToken cancellationToken, ArtistMetadataProviderGate? providerGate,
        bool forceProviderRefresh = false, bool? ocrTextArtBlockingEnabled = null, bool retainEnrichment = false)
        => (await RefreshArtistWithOutcomeAsync(new(artistId, null, source, includePopularSongs,
            includeDiscography, forceProviderRefresh, ocrTextArtBlockingEnabled,
            retainEnrichment ? ArtistCacheRefreshScope.Full : ArtistCacheRefreshScope.Full & ~ArtistCacheRefreshScope.Enrichment),
            cancellationToken)).IsComplete;

    public Task SaveRunResultAsync(string runId, long? artistId, ArtistMetadataCacheRefreshResult result, CancellationToken token)
    {
        if (_artistPageCache is null) throw new InvalidOperationException("Refresh run storage is unavailable.");
        return _artistPageCache.UpsertRequiredAsync("artist-refresh-run", runId,
            JsonSerializer.Serialize(new ArtistCacheRunResult(runId, artistId, result, DateTimeOffset.UtcNow)), DateTimeOffset.UtcNow, token);
    }

    public async Task<ArtistCacheRunResult?> GetRunResultAsync(string runId, CancellationToken token)
    {
        if (_artistPageCache is null) return null;
        var row = await _artistPageCache.TryGetRequiredAsync("artist-refresh-run", runId, token);
        return row is null ? null : JsonSerializer.Deserialize<ArtistCacheRunResult>(row.PayloadJson);
    }

    public async Task<ArtistMetadataCacheRefreshRequest> BuildAutomaticRequestAsync(long artistId,
        ArtistCacheRefreshScope scope, string? source = null)
    {
        var preferences = await _preferences.LoadAsync();
        return new(artistId, null, source ?? preferences.MetadataUpdaterSource,
            preferences.MetadataUpdaterIncludePopularSongs, preferences.MetadataUpdaterIncludeDiscography,
            OcrTextArtBlockingEnabled: preferences.MetadataUpdaterOcrTextArtBlocking, Scope: scope);
    }

    private async Task<string> BuildFingerprintAsync(ArtistMetadataCacheRefreshRequest request, ArtistDetailDto artist,
        IReadOnlyList<string> names, UserPreferencesDto preferences, CancellationToken token, string provider, string stage)
    {
        var identities = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var identitySource in new[] { "spotify", "deezer", "apple", "tidal", "qobuz", "audiomack", "musicbrainz" })
            identities[identitySource] = await _repository.GetArtistSourceIdsAsync(artist.Id, identitySource, token);
        return JsonSerializer.Serialize(new { Provider = provider, Stage = stage,
            IncludeDiscography = stage is "metadata" or "extras" && request.IncludeDiscography,
            IncludePopularSongs = stage == "metadata" && request.IncludePopularSongs,
            ArtworkEnabled = stage != "artwork" || _artworkCatalog.GetRequestedProviders(provider).Contains(provider),
            Ocr = stage == "artwork" && (request.OcrTextArtBlockingEnabled ?? preferences.MetadataUpdaterOcrTextArtBlocking),
            Identities = identities.Where(pair => pair.Key == provider).ToArray(), Names = names });
    }

    private IReadOnlyList<string> RequestedArtworkProviders(string source)
    {
        var enabled = _artworkCatalog.GetRequestedProviders(source == "auto" ? null : source);
        return enabled.Count > 0 ? enabled : [source == "auto" ? "local" : source];
    }

    private static IReadOnlyList<string> RequestedEnrichmentProviders(string source)
        => source == "auto" ? ["musicbrainz", "audiomack"] : source == "audiomack" ? ["audiomack"] : ["local"];

    private IEnumerable<(string Provider, string Stage)> RequestedStages(ArtistMetadataCacheRefreshRequest request)
    {
        var selected = ParseProvider(request.Source);
        var source = selected.HasValue ? ProviderName(selected.Value) : "auto";
        if (request.Scope.HasFlag(ArtistCacheRefreshScope.Artwork))
            foreach (var provider in RequestedArtworkProviders(source)) yield return (provider, "artwork");
        if (request.Scope.HasFlag(ArtistCacheRefreshScope.Biographies))
            foreach (var provider in selected.HasValue ? [selected.Value] : BiographyProviders) yield return (ProviderName(provider), "biographies");
        if (request.Scope.HasFlag(ArtistCacheRefreshScope.ProviderMetadata) && selected is null or BiographyProvider.Spotify
            && (request.IncludeDiscography || request.IncludePopularSongs)) yield return ("spotify", "metadata");
        if (request.Scope.HasFlag(ArtistCacheRefreshScope.MediaExtras) && request.IncludeDiscography)
            foreach (var provider in new[] { "apple", "tidal" }.Where(provider => source == "auto" || source == provider)) yield return (provider, "extras");
        if (request.Scope.HasFlag(ArtistCacheRefreshScope.Enrichment))
            foreach (var provider in RequestedEnrichmentProviders(source)) yield return (provider, "enrichment");
    }

    public async Task<bool> IsCompleteAsync(ArtistMetadataCacheRefreshRequest request, CancellationToken token)
    {
        if (_artistPageCache is null || request.ArtistId is not > 0) return false;
        var artist = await _repository.GetArtistAsync(request.ArtistId.Value, token);
        if (artist is null) return true;
        var names = await ResolveLookupNamesAsync(artist.Name, token);
        var preferences = await _preferences.LoadAsync();
        var stages = RequestedStages(request).ToArray();
        if (stages.Length == 0) return false;
        foreach (var (provider, stage) in stages)
        {
            var fingerprint = await BuildFingerprintAsync(request, artist, names, preferences, token, provider, stage);
            var row = await _artistPageCache.TryGetRequiredAsync(ManifestSource, $"{artist.Id}:{provider}:{stage}", token);
            if (row is null) return false;
            ArtistCacheStageManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<ArtistCacheStageManifest>(row.PayloadJson); }
            catch (JsonException) { return false; }
            if (manifest is not { Version: 1, Outcome.IsComplete: true } || manifest.Fingerprint != fingerprint
                || manifest.CompletedAtUtc <= DateTimeOffset.UtcNow.AddDays(-7)) return false;
            if (stage == "artwork" && manifest.Outcome.Status != ArtistCacheWorkStatus.Disabled && !await _artworkCatalog.HasCompleteArtworkAsync(artist.Id, provider, token)) return false;
        }
        return true;
    }

    public async Task<ArtistCacheRefreshOutcome> RefreshArtistWithOutcomeAsync(
        ArtistMetadataCacheRefreshRequest request, CancellationToken cancellationToken)
    {
        if (request.ArtistId is not > 0) throw new ArgumentException("ArtistId is required.", nameof(request));
        var artistId = request.ArtistId.Value;
        var exclusion = _artistLocks.GetOrAdd(artistId, _ => new SemaphoreSlim(1, 1));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ArtistRefreshTimeout);
        try { await exclusion.WaitAsync(deadline.Token); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Artist refresh timed out waiting for previous work to finish.", ex); }
        var work = new List<ArtistCacheWorkOutcome>();
        var operations = new List<Task>();
        using var capture = _providerGate.CaptureOperations(operations);
        try
        {
            var token = deadline.Token;
            var artist = await _repository.GetArtistAsync(artistId, token);
            if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
                return new(artistId, [new("local", "artist", ArtistCacheWorkStatus.Failed, ReasonCode: "artist-unavailable")]);
            var preferences = await _preferences.LoadAsync();
            var selected = ParseProvider(request.Source);
            var normalized = selected.HasValue ? ProviderName(selected.Value) : "auto";
            var names = await ResolveLookupNamesAsync(artist.Name, token);
            async Task Stage(string provider, string stage, Func<Task<ArtistCacheWorkOutcome>> action)
            {
                var key = $"{artistId}:{provider}:{stage}";
                var fingerprint = await BuildFingerprintAsync(request, artist, names, preferences, token, provider, stage);
                ArtistCacheStageManifest? previous = null;
                if (_artistPageCache is not null)
                {
                    var cached = await _artistPageCache.TryGetRequiredAsync(ManifestSource, key, token);
                    if (cached is not null)
                    {
                        try { previous = JsonSerializer.Deserialize<ArtistCacheStageManifest>(cached.PayloadJson); }
                        catch (JsonException) { /* Legacy/incomplete evidence is unknown, not success. */ }
                    }
                }
                if (!request.ForceProviderRefresh && previous is { Version: 1, Outcome.IsComplete: true }
                    && previous.Fingerprint == fingerprint && previous.CompletedAtUtc > DateTimeOffset.UtcNow.AddDays(-7)
                    && (stage != "artwork" || previous.Outcome.Status == ArtistCacheWorkStatus.Disabled || await _artworkCatalog.HasCompleteArtworkAsync(artistId, provider, token)))
                {
                    work.Add(previous.Outcome);
                    return;
                }
                var attemptedAt = DateTimeOffset.UtcNow;
                ArtistCacheWorkOutcome outcome;
                try { outcome = await action(); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var status = ex switch
                    {
                        OperationCanceledException or TimeoutException => ArtistCacheWorkStatus.TimedOut,
                        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => ArtistCacheWorkStatus.AuthenticationRequired,
                        _ when ArtistMetadataProviderGate.IsRateLimited(ex) => ArtistCacheWorkStatus.Deferred,
                        _ => ArtistCacheWorkStatus.Failed
                    };
                    outcome = new(provider, stage, status, ReasonCode: ex.GetType().Name,
                        RetryAtUtc: status == ArtistCacheWorkStatus.Deferred ? DateTimeOffset.UtcNow.AddMinutes(10) : null);
                    _logger.LogWarning(ex, "Artist {ArtistId} cache stage {Provider}/{Stage} remains unresolved.", artistId, provider, stage);
                }
                work.Add(outcome);
                if (!outcome.IsComplete)
                    _configStore?.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "warn",
                        $"Artist cache refresh incomplete for {artist.Name}: {provider}/{stage}, {outcome.Status}, {outcome.ReasonCode}."));
                if (_artistPageCache is null) throw new InvalidOperationException("Required artist refresh evidence storage is unavailable.");
                ArtistArtworkObservation? observation = null;
                if (stage == "artwork")
                {
                    var collected = await _artistPageCache.TryGetRequiredAsync("artist-artwork-observation", $"{artistId}:{provider}", token);
                    if (collected is not null && collected.FetchedUtc >= attemptedAt)
                        observation = JsonSerializer.Deserialize<ArtistArtworkObservation>(collected.PayloadJson);
                }
                await _artistPageCache.UpsertRequiredAsync(ManifestSource, key,
                    JsonSerializer.Serialize(new ArtistCacheStageManifest(1, await BuildFingerprintAsync(request, artist, names, preferences, token, provider, stage), outcome, attemptedAt,
                        outcome.IsComplete ? DateTimeOffset.UtcNow : previous?.CompletedAtUtc,
                        observation?.CandidateIdentities, observation?.RejectedIdentities, observation?.CandidateReasons)), DateTimeOffset.UtcNow, token);
            }

            if (request.Scope.HasFlag(ArtistCacheRefreshScope.Artwork))
            {
                foreach (var provider in RequestedArtworkProviders(normalized))
                {
                    await Stage(provider, "artwork", async () =>
                    {
                        if (!_artworkCatalog.GetRequestedProviders(normalized == "auto" ? null : normalized).Contains(provider))
                            return new(provider, "artwork", ArtistCacheWorkStatus.Disabled);
                        if (provider == "spotify" && names.Count > 1)
                            await EnsureAliasSpotifyIdentitiesAsync(artistId, artist.Name, _providerGate, token);
                        var results = await _artworkCatalog.RefreshAsync(artistId, artist.Name, artist.PreferredImagePath, token,
                            provider, forceProviderRefresh: true, providerGate: _providerGate,
                            includeGallery: true, allowArtistPageScrape: false, artistLookupNames: names);
                        var result = results.FirstOrDefault(item => item.Provider == provider);
                        return result?.Outcome ?? new(provider, "artwork", ArtistCacheWorkStatus.Disabled);
                    });
                }
                await _visualSlots.ApplyCatalogVisualsToSlotsAsync(artistId, artist.Name,
                    request.OcrTextArtBlockingEnabled ?? preferences.MetadataUpdaterOcrTextArtBlocking, token);
            }
            if (request.Scope.HasFlag(ArtistCacheRefreshScope.Biographies))
            {
                var wroteBiography = false;
                foreach (var provider in selected.HasValue ? [selected.Value] : BiographyProviders)
                {
                    var name = ProviderName(provider);
                    await Stage(name, "biographies", async () =>
                    {
                        if (provider == BiographyProvider.Deezer) return new(name, "biographies", ArtistCacheWorkStatus.NotApplicable);
                        if (provider == BiographyProvider.Spotify && names.Count > 1)
                            await EnsureAliasSpotifyIdentitiesAsync(artistId, artist.Name, _providerGate, token);
                        await _artworkCatalog.EnsureProviderIdentityAsync(artistId, artist.Name, name, names, token);
                        var wrote = await CacheBiographiesAsync(artistId, artist.Name, provider, names, _providerGate, token);
                        wroteBiography |= wrote;
                        return new(name, "biographies", wrote ? ArtistCacheWorkStatus.Succeeded : ArtistCacheWorkStatus.NoData);
                    });
                }
                if (wroteBiography) await _repository.SelectArtistBiographySourceAsync(artistId,
                    selected.HasValue ? ProviderName(selected.Value) : null, token);
            }
            if (request.Scope.HasFlag(ArtistCacheRefreshScope.ProviderMetadata)
                && (selected is null or BiographyProvider.Spotify) && (request.IncludePopularSongs || request.IncludeDiscography))
            {
                await Stage("spotify", "metadata", async () =>
                {
                    var page = await _providerGate.RunRequiredAsync("spotify", ct => _spotify.GetArtistPageAsync(artistId, artist.Name,
                        true, false, ct, includeDeezerLinking: false, includeDiscography: request.IncludeDiscography), token);
                    return new("spotify", "metadata", page is null ? ArtistCacheWorkStatus.Failed : ArtistCacheWorkStatus.Succeeded);
                });
            }
            if (request.Scope.HasFlag(ArtistCacheRefreshScope.MediaExtras) && request.IncludeDiscography)
            {
                foreach (var provider in new[] { "apple", "tidal" }.Where(provider => normalized == "auto" || normalized == provider))
                    await Stage(provider, "extras", async () =>
                    {
                        await _providerGate.RunRequiredAsync(provider, async ct =>
                        {
                            if (provider == "apple") await _mediaExtras.RefreshAppleAsync(artistId, artist.Name, ct, request.ForceProviderRefresh);
                            else await _mediaExtras.RefreshTidalAsync(artistId, artist.Name, ct, request.ForceProviderRefresh);
                            return true;
                        }, token);
                        return new(provider, "extras", ArtistCacheWorkStatus.Succeeded);
                    });
            }
            if (request.Scope.HasFlag(ArtistCacheRefreshScope.Enrichment))
            {
                var locationResolved = false;
                foreach (var provider in RequestedEnrichmentProviders(normalized))
                {
                    await Stage(provider, "enrichment", async () =>
                    {
                        if (_locationResolver is null || provider == "local" || locationResolved)
                            return new(provider, "enrichment", ArtistCacheWorkStatus.NotApplicable);
                        var wrote = await RetainArtistEnrichmentAsync(artistId, artist.Name, provider, token);
                        return new(provider, "enrichment", wrote ? ArtistCacheWorkStatus.Succeeded : ArtistCacheWorkStatus.NoData);
                    });
                    locationResolved |= work.Last().Status == ArtistCacheWorkStatus.Succeeded;
                }
            }
            foreach (var outcome in work.Where(item => item.IsComplete))
            {
                var key = $"{artistId}:{outcome.Provider}:{outcome.Stage}";
                var row = await _artistPageCache!.TryGetRequiredAsync(ManifestSource, key, token);
                if (row is not null && JsonSerializer.Deserialize<ArtistCacheStageManifest>(row.PayloadJson) is { } manifest)
                    await _artistPageCache.UpsertRequiredAsync(ManifestSource, key,
                        JsonSerializer.Serialize(manifest with { Fingerprint = await BuildFingerprintAsync(request, artist, names, preferences, token, outcome.Provider, outcome.Stage) }), DateTimeOffset.UtcNow, token);
            }
            return new(artistId, work);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Artist cache refresh exceeded its existing per-artist deadline.", ex); }
        finally
        {
            Task[] pending;
            lock (operations) pending = operations.Where(operation => !operation.IsCompleted).ToArray();
            if (pending.Length == 0) exclusion.Release();
            else _ = ReleaseArtistAfterCompletionAsync(pending, exclusion);
        }
    }

    private static async Task ReleaseArtistAfterCompletionAsync(Task[] operations, SemaphoreSlim exclusion)
    {
        try { await Task.WhenAll(operations); }
        catch (Exception ex) when (ex is not StackOverflowException && ex is not OutOfMemoryException)
        { /* Provider failure is recorded by the owning stage; observe late faults before releasing. */ }
        finally { exclusion.Release(); }
    }

    /// <summary>
    /// The ordered provider lookup names for one artist: the canonical/preferred name first,
    /// then its aliases, deduplicated case-insensitively. A normal artist returns a single
    /// name and therefore never reaches the uncached alias-group read.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveLookupNamesAsync(
        string canonicalArtistName,
        CancellationToken cancellationToken)
    {
        var single = new[] { canonicalArtistName };
        var aliasService = _artistAliasService;
        if (aliasService is null || !aliasService.IsConfigured)
        {
            return single;
        }

        try
        {
            // Cached snapshot: cheap enough to probe for every artist in a run.
            var aliasMap = await aliasService.GetAliasMapAsync(cancellationToken).ConfigureAwait(false);
            if (!aliasMap.ContainsKey(ArtistAliasService.NormalizeName(canonicalArtistName)))
            {
                return single;
            }

            var groupNames = await aliasService.GetGroupNamesAsync(canonicalArtistName, cancellationToken).ConfigureAwait(false);
            var ordered = new List<string>(groupNames.Count) { canonicalArtistName };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonicalArtistName };
            foreach (var aliasName in groupNames
                .Select(name => (name ?? string.Empty).Trim())
                .Where(trimmed => trimmed.Length > 0 && seen.Add(trimmed)))
            {
                ordered.Add(aliasName);
            }

            return ordered;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Artist alias lookup names unavailable for {ArtistName}; using the canonical name only.", canonicalArtistName);
            return single;
        }
    }

    /// <summary>
    /// Deliberate, provider-gated alias-aware Spotify identity discovery for an alias-managed
    /// artist. Failures are contained: the refresh continues with whatever identities are stored.
    /// </summary>
    private async Task EnsureAliasSpotifyIdentitiesAsync(
        long artistId,
        string canonicalArtistName,
        ArtistMetadataProviderGate gate,
        CancellationToken cancellationToken)
    {
        var providerName = ProviderName(BiographyProvider.Spotify);
        if (gate.IsUnavailable(providerName))
        {
            return;
        }

        try
        {
            await gate.RunRequiredAsync(
                providerName,
                async token =>
                {
                    await _spotify.EnsureAliasSpotifyIdentitiesAsync(artistId, canonicalArtistName, token);
                    return true;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Discovery is best-effort: the refresh continues with whatever identities are
            // already stored rather than failing the artist.
            _logger.LogWarning(ex, "Alias Spotify identity discovery failed for artist {ArtistId}.", artistId);
            throw;
        }
    }

    public async Task<bool> RefreshBiographiesAsync(long artistId, string artistName, string? source,
        CancellationToken cancellationToken, ArtistMetadataProviderGate? providerGate = null)
        => (await RefreshArtistWithOutcomeAsync(new(artistId, null, source, ForceProviderRefresh: true,
            Scope: ArtistCacheRefreshScope.Biographies), cancellationToken)).IsComplete;

    private async Task<bool> CacheBiographiesAsync(long artistId, string artistName,
        BiographyProvider? selectedProvider, IReadOnlyList<string> names, ArtistMetadataProviderGate gate, CancellationToken token)
    {
        var wrote = false;
        IReadOnlyList<BiographyProvider> providers = selectedProvider.HasValue ? [selectedProvider.Value] : BiographyProviders;
        foreach (var provider in providers)
        {
            var source = ProviderName(provider);
            if (gate.IsUnavailable(source)) throw new HttpRequestException(ArtistMetadataProviderGate.UnavailableMessage, null, System.Net.HttpStatusCode.TooManyRequests);
            if (provider == BiographyProvider.LastFm)
            {
                foreach (var name in names)
                {
                    if (gate.IsUnavailable(source)) throw new HttpRequestException(ArtistMetadataProviderGate.UnavailableMessage, null, System.Net.HttpStatusCode.TooManyRequests);
                    var result = await gate.RunRequiredAsync(source, ct => _lastFm.GetArtistBiographyAsync(name, ct, propagateRateLimits: true), token);
                    var text = ArtistBiographySanitizer.Clean(result?.Biography);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    await _repository.UpsertArtistBiographyCacheAsync(artistId, source, result!.Name, text, false, result.Name, name, token);
                    wrote = true;
                }
                continue;
            }
            var identities = await _repository.GetArtistSourceIdentitiesAsync(artistId, source, token);
            var eligible = identities.Where(identity => identity.VerificationState is "verified" or "user_confirmed").ToList();
            if (names.Count <= 1) eligible = eligible.Take(1).ToList();
            if (eligible.Count == 0 && identities.Count > 0) throw new InvalidOperationException("Provider artist identity is unverified.");
            if (eligible.Count == 0)
            {
                // Preserve the established initial-discovery path, but store its real identity.
                var text = ArtistBiographySanitizer.Clean(await gate.RunRequiredAsync(source,
                    ct => ResolveBiographyAsync(provider, artistId, artistName, ct, names), token));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var id = await _repository.GetArtistSourceIdAsync(artistId, source, token) ?? "";
                    await _repository.UpsertArtistBiographyCacheAsync(artistId, source, id, text, false, null, null, token);
                    wrote = true;
                }
                continue;
            }
            foreach (var identity in eligible)
            {
                if (gate.IsUnavailable(source)) throw new HttpRequestException(ArtistMetadataProviderGate.UnavailableMessage, null, System.Net.HttpStatusCode.TooManyRequests);
                var profile = await gate.RunRequiredAsync(source,
                    ct => ResolveProfileBiographyAsync(provider, artistId, artistName, identity.SourceId, ct), token);
                var text = ArtistBiographySanitizer.Clean(profile.Biography);
                if (string.IsNullOrWhiteSpace(text)) continue;
                await _repository.UpsertArtistBiographyCacheAsync(artistId, source, identity.SourceId, text, false,
                    profile.NativeName ?? identity.NativeName, identity.AliasName, token);
                wrote = true;
            }
        }
        return wrote;
    }

    private async Task<(string? Biography, string? NativeName)> ResolveProfileBiographyAsync(BiographyProvider provider, long artistId,
        string artistName, string sourceId, CancellationToken token)
    {
        try
        {
            if (provider == BiographyProvider.Spotify)
            {
                var profile = await _spotify.GetSiblingArtistPageAsync(artistId, sourceId, artistName, token, forceRefresh: true);
                if (profile?.Artist is null) throw new InvalidOperationException("Spotify biography profile could not be verified.");
                return (profile.Artist.Biography, profile.Artist.Name);
            }
            var biography = provider switch
            {
                BiographyProvider.Apple => (await _apple.ResolveByArtistIdAsync(sourceId, artistName, token, propagatePageErrors: true))?.Biography,
                BiographyProvider.Tidal => await ResolveTidalBiographyByIdAsync(sourceId, token),
                BiographyProvider.Qobuz => await ResolveQobuzBiographyByIdAsync(sourceId, token),
                BiographyProvider.Audiomack => await _audiomack.ResolveBiographyBySlugAsync(sourceId, artistName, token, requireFresh: true),
                _ => null
            };
            return (biography, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ArtistMetadataProviderGate.IsRateLimited(ex))
        {
            _logger.LogWarning(ex, "Biography lookup failed for {Provider} profile {SourceId}.", provider, sourceId);
            throw;
        }
    }

    private async Task<string?> ResolveBiographyAsync(
        BiographyProvider provider,
        long artistId,
        string artistName,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? lookupNames = null)
    {
        try
        {
            return provider switch
            {
                BiographyProvider.Spotify => await ResolveSpotifyBiographyAsync(artistId, artistName, lookupNames, cancellationToken),
                BiographyProvider.Apple => await ResolveAppleBiographyAsync(artistId, artistName, lookupNames, cancellationToken),
                BiographyProvider.Tidal => await ResolveTidalBiographyAsync(artistId, cancellationToken),
                BiographyProvider.Qobuz => await ResolveQobuzBiographyAsync(artistId, cancellationToken),
                BiographyProvider.Audiomack => (await _audiomack.ResolveProfileRequiredAsync(artistId, artistName, cancellationToken, lookupNames))?.Biography,
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported biography provider.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!ArtistMetadataProviderGate.IsRateLimited(ex))
        {
            _logger.LogWarning(
                ex,
                "Artist biography provider {Provider} failed for artist {ArtistId} ({ArtistName}).",
                provider,
                artistId,
                artistName);
            throw;
        }
    }

    private static IReadOnlyList<string> EffectiveLookupNames(string artistName, IReadOnlyList<string>? lookupNames)
        => lookupNames is { Count: > 0 } ? lookupNames : [artistName];

    /// <summary>
    /// Fill-only fallback: the primary Spotify identity stays authoritative, and a verified
    /// sibling identity may only supply a biography when the primary has none. Two conflicting
    /// non-empty biographies are never combined or reordered.
    /// </summary>
    private async Task<string?> ResolveSpotifyBiographyAsync(
        long artistId,
        string artistName,
        IReadOnlyList<string>? lookupNames,
        CancellationToken cancellationToken)
    {
        var page = await _spotify.GetArtistPageAsync(
            artistId,
            artistName,
            forceRefresh: true,
            forceRematch: false,
            cancellationToken,
            includeDeezerLinking: false,
            includeDiscography: false);
        if (page?.Artist is null) throw new InvalidOperationException("Spotify biography profile could not be verified.");
        var biography = page.Artist.Biography;
        if (!string.IsNullOrWhiteSpace(biography))
        {
            return biography;
        }

        // A normal artist has exactly one lookup name, so no sibling scan happens for it.
        if (EffectiveLookupNames(artistName, lookupNames).Count <= 1)
        {
            return biography;
        }

        var siblingBiography = await _spotify.GetAliasIdentityBiographyAsync(
            artistId,
            page?.Artist?.Id,
            artistName,
            cancellationToken);
        return string.IsNullOrWhiteSpace(siblingBiography) ? biography : siblingBiography;
    }

    private async Task<string?> ResolveAppleBiographyAsync(long artistId, string artistName, IReadOnlyList<string>? lookupNames, CancellationToken cancellationToken)
    {
        var appleId = await _repository.GetArtistSourceIdAsync(artistId, AppleSource, cancellationToken);
        AppleArtistBiographyResult? result;
        if (!string.IsNullOrWhiteSpace(appleId))
        {
            // A stored Apple id is authoritative: no alias discovery, exactly as today.
            result = await _apple.ResolveByArtistIdAsync(
                appleId,
                artistName,
                cancellationToken,
                propagatePageErrors: true);
        }
        else
        {
            // Discovery only. Each candidate name keeps the unchanged exact-name/title validation,
            // and the first verified Apple id wins; later refreshes take the id path above.
            var tracks = await _repository.GetArtistTrackTitlesAsync(artistId, 8, cancellationToken);
            result = null;
            foreach (var name in EffectiveLookupNames(artistName, lookupNames))
            {
                var attempt = await _apple.ResolveByExactArtistNameAndTracksAsync(name, tracks, cancellationToken, propagatePageErrors: true);
                if (!string.IsNullOrWhiteSpace(attempt?.AppleId))
                {
                    result = attempt;
                    await _repository.UpsertArtistSourceIdAsync(artistId, AppleSource, result.AppleId, cancellationToken);
                    break;
                }
            }
        }

        await _repository.UpdateArtistAppleBiographyAsync(artistId, result?.Biography, DateTimeOffset.UtcNow, cancellationToken);
        return result?.Biography;
    }

    private async Task<string?> ResolveQobuzBiographyAsync(long artistId, CancellationToken cancellationToken)
    {
        var sourceId = await _repository.GetArtistSourceIdAsync(artistId, QobuzSource, cancellationToken);
        return await ResolveQobuzBiographyByIdAsync(sourceId, cancellationToken);
    }

    private async Task<string?> ResolveQobuzBiographyByIdAsync(string? sourceId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(sourceId, out var qobuzId) || qobuzId <= 0)
            throw new InvalidOperationException("Qobuz artist identity is unresolved.");

        var artist = await _qobuz.GetArtistAsync(qobuzId, "us-en", cancellationToken, forceRefresh: true);
        return FirstNonEmpty(artist?.Biography?.Content, artist?.Biography?.Summary);
    }

    private async Task<string?> ResolveTidalBiographyAsync(long artistId, CancellationToken cancellationToken)
    {
        var sourceId = await _repository.GetArtistSourceIdAsync(artistId, TidalSource, cancellationToken);
        return await ResolveTidalBiographyByIdAsync(sourceId, cancellationToken);
    }

    private async Task<string?> ResolveTidalBiographyByIdAsync(string? sourceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("Tidal artist identity is unresolved.");

        var token = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
        var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken) ?? "US";
        var url = $"https://openapi.tidal.com/v2/artists/{Uri.EscapeDataString(sourceId)}?countryCode={Uri.EscapeDataString(country)}&include=biography";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClients.CreateClient().SendAsync(request, cancellationToken);
        ArtistMetadataProviderGate.ThrowIfRateLimited(response);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Tidal biography lookup rejected with HTTP {StatusCode} for profile {SourceId}; check Tidal credentials/openapi access.",
                    (int)response.StatusCode,
                    sourceId);
            }

            response.EnsureSuccessStatusCode();
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var biographyText = TidalBiographyParser.TryReadBiographyText(document.RootElement);
        return string.IsNullOrWhiteSpace(biographyText)
            ? null
            : biographyText;
    }

    private static BiographyProvider? ParseProvider(string? source)
    {
        var normalized = (source ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "deezer" => BiographyProvider.Deezer,
            SpotifySource => BiographyProvider.Spotify,
            AppleSource => BiographyProvider.Apple,
            TidalSource => BiographyProvider.Tidal,
            QobuzSource => BiographyProvider.Qobuz,
            "lastfm" => BiographyProvider.LastFm,
            "audiomack" => BiographyProvider.Audiomack,
            _ => null
        };
    }

    private static string ProviderName(BiographyProvider provider)
        => provider switch
        {
            BiographyProvider.Deezer => "deezer",
            BiographyProvider.Spotify => SpotifySource,
            BiographyProvider.Apple => AppleSource,
            BiographyProvider.Tidal => TidalSource,
            BiographyProvider.Qobuz => QobuzSource,
            BiographyProvider.LastFm => "lastfm",
            BiographyProvider.Audiomack => "audiomack",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported biography provider.")
        };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? GetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record ArtistMetadataCacheRefreshRequest(
    long? ArtistId,
    long? FolderId,
    string? Source,
    bool IncludePopularSongs = false,
    bool IncludeDiscography = false,
    bool ForceProviderRefresh = false,
    bool? OcrTextArtBlockingEnabled = null,
    ArtistCacheRefreshScope Scope = ArtistCacheRefreshScope.Full);
public sealed record ArtistMetadataCacheRefreshResult(int Total, int Succeeded, int Failed, string? Error);
public sealed record ArtistMetadataOperationProgress(int Processed, int Total, string? CurrentArtist, long? CompletedArtistId = null, int Succeeded = 0, int Failed = 0);


[Flags]
public enum ArtistCacheRefreshScope
{
    Artwork = 1, Biographies = 2, ProviderMetadata = 4, MediaExtras = 8, Enrichment = 16,
    Full = Artwork | Biographies | ProviderMetadata | MediaExtras | Enrichment
}

public enum ArtistCacheWorkStatus
{
    Succeeded, NoData, NotApplicable, Disabled, Deferred, AuthenticationRequired, Failed, TimedOut, Cancelled
}

public sealed record ArtistCacheWorkOutcome(string Provider, string Stage, ArtistCacheWorkStatus Status,
    int CandidateCount = 0, int CachedCount = 0, string? ReasonCode = null, DateTimeOffset? RetryAtUtc = null)
{
    public bool IsComplete => Status is ArtistCacheWorkStatus.Succeeded or ArtistCacheWorkStatus.NoData
        or ArtistCacheWorkStatus.NotApplicable or ArtistCacheWorkStatus.Disabled;
}

public sealed record ArtistCacheRefreshOutcome(long ArtistId, IReadOnlyList<ArtistCacheWorkOutcome> Work)
{
    public bool IsComplete => Work.Count > 0 && Work.All(item => item.IsComplete);
    public IReadOnlyList<ArtistCacheWorkOutcome> UnresolvedWork => Work.Where(item => !item.IsComplete).ToList();
}

public sealed record ArtistCacheStageManifest(int Version, string Fingerprint, ArtistCacheWorkOutcome Outcome,
    DateTimeOffset AttemptedAtUtc, DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<string>? CandidateIdentities = null, IReadOnlyList<string>? RejectedIdentities = null,
    IReadOnlyDictionary<string, string>? CandidateReasons = null);

public sealed record ArtistCacheRunResult(string RunId, long? ArtistId, ArtistMetadataCacheRefreshResult Result, DateTimeOffset CompletedAtUtc);
