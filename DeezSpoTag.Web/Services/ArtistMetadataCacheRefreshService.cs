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
        ArtistPageCacheRepository? artistPageCache = null)
    {
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
        var priorOutcomes = resumedRun?.Outcomes;
        var prior = CountOutcomes(priorOutcomes);
        var completed = resumedRun?.Outcomes is { Count: > 0 }
            ? ArtistRunOutcomes.BuildResumeSkipSet(priorOutcomes!)
            : new HashSet<long>();
        var processed = completed.Intersect(targetArtistIds).Count();
        var succeeded = prior.Succeeded;
        var failed = prior.Failed;
        var gate = new ArtistMetadataProviderGate(_logger);
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Contains(artist.Id))
            {
                continue;
            }

            processed++;
            progress?.Report(new ArtistMetadataOperationProgress(
                processed, total, artist.Name, null, succeeded, failed));
            using var artistCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool>? refreshTask = null;
            try
            {
                refreshTask = RefreshArtistAsync(
                    artist.Id,
                    artist.Name,
                    request.Source,
                    request.IncludePopularSongs,
                    request.IncludeDiscography,
                    artistCancellation.Token,
                    gate,
                    request.ForceProviderRefresh,
                    request.OcrTextArtBlockingEnabled);
                await refreshTask.WaitAsync(ArtistRefreshTimeout, cancellationToken);
                succeeded++;
                outcomeSink?.Invoke(new ArtistRunOutcomeRecord(artist.Id, ArtistRunOutcomes.Succeeded));
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
                artistCancellation.CancelAfter(TimeSpan.Zero);
                ObserveLateCompletion(refreshTask);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                outcomeSink?.Invoke(new ArtistRunOutcomeRecord(artist.Id, ArtistRunOutcomes.Failed));
                progress?.Report(new ArtistMetadataOperationProgress(
                    processed, total, artist.Name, artist.Id, succeeded, failed));
                _logger.LogWarning(ex, "Artist metadata cache refresh failed for artist {ArtistId}.", artist.Id);
            }

            try
            {
                await RetainArtistEnrichmentAsync(artist.Id, artist.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not retain artist enrichment metadata for artist {ArtistId}.", artist.Id);
            }

            await Task.Delay(ArtistYield, cancellationToken);
        }

        return new ArtistMetadataCacheRefreshResult(total, succeeded, failed, null);
    }

    private async Task RetainArtistEnrichmentAsync(long artistId, string artistName, CancellationToken cancellationToken)
    {
        if (_locationResolver is null || _artistPageCache is null)
        {
            return;
        }

        var location = await _locationResolver.ResolveAsync(artistId, artistName, cancellationToken).ConfigureAwait(false);
        if (location is null)
        {
            return;
        }

        IReadOnlyList<DeezSpoTag.Core.Models.LanguageMetadataEvidence> languages = [];
        var retainedEntry = await _artistPageCache.TryGetAsync("artist-enrichment",
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
        await _artistPageCache.UpsertAsync(
            "artist-enrichment",
            artistId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new { version = "v1", metadata, retainedAt = DateTimeOffset.UtcNow }),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
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

    private static void ObserveLateCompletion(Task? operation)
    {
        if (operation is not null && !operation.IsCompletedSuccessfully)
        {
            _ = operation.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
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
        long artistId,
        string artistName,
        string? source,
        bool includePopularSongs,
        bool includeDiscography,
        CancellationToken cancellationToken,
        ArtistMetadataProviderGate? providerGate,
        bool forceProviderRefresh = false,
        bool? ocrTextArtBlockingEnabled = null)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName))
        {
            return false;
        }

        var gate = providerGate ?? new ArtistMetadataProviderGate(_logger);
        var selectedProvider = ParseProvider(source);
        var normalizedSource = selectedProvider.HasValue
            ? ProviderName(selectedProvider.Value)
            : "auto";
        var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null)
        {
            return false;
        }

        // The row just read is the authority for the artist name. The caller-supplied name can
        // come from a queue snapshot taken before an Artist Alias merge, so it is only used to
        // get here. Everything below provider-facing uses the current DB value.
        var canonicalArtistName = (artist.Name ?? string.Empty).Trim();
        if (canonicalArtistName.Length == 0)
        {
            return false;
        }

        // Resolved once per artist. A normal artist gets the single canonical name and never
        // pays for the uncached alias-group read; only an alias-managed artist does.
        var lookupNames = await ResolveLookupNamesAsync(canonicalArtistName, cancellationToken);
        var aliasManaged = lookupNames.Count > 1;

        if (aliasManaged)
        {
            // Spotify is the only provider where one real artist can legitimately have several
            // remote identities, so the alias-aware identity discovery is invoked deliberately
            // here instead of being left to whichever path happens to touch Spotify first.
            await EnsureAliasSpotifyIdentitiesAsync(artistId, canonicalArtistName, gate, cancellationToken);
        }

        await _artworkCatalog.RefreshAsync(
            artistId,
            canonicalArtistName,
            artist.PreferredImagePath,
            cancellationToken,
            normalizedSource == "auto" ? null : normalizedSource,
            forceProviderRefresh: forceProviderRefresh,
            providerGate: gate,
            includeGallery: false,
            allowArtistPageScrape: false,
            artistLookupNames: lookupNames);
        var preferences = await _preferences.LoadAsync();
        await _visualSlots.ApplyCatalogVisualsToSlotsAsync(
            artistId,
            canonicalArtistName,
            ocrTextArtBlockingEnabled ?? preferences.MetadataUpdaterOcrTextArtBlocking,
            cancellationToken);
        IReadOnlyList<BiographyProvider> requestedProviders = selectedProvider.HasValue
            ? [selectedProvider.Value]
            : BiographyProviders;

        if (includeDiscography && !gate.IsUnavailable(ProviderName(BiographyProvider.Spotify)))
        {
            await gate.RunAsync(
                ProviderName(BiographyProvider.Spotify),
                token => _spotify.GetArtistPageAsync(
                    artistId,
                    canonicalArtistName,
                    forceRefresh: includeDiscography,
                    forceRematch: false,
                    token,
                    includeDeezerLinking: false,
                    includeDiscography: includeDiscography),
                cancellationToken);
        }

        var biographies = new List<(BiographyProvider Provider, string Biography)>();
        foreach (var provider in requestedProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerName = ProviderName(provider);
            if (gate.IsUnavailable(providerName))
            {
                continue;
            }

            var biography = ArtistBiographySanitizer.Clean(await gate.RunAsync(
                providerName,
                token => ResolveBiographyAsync(provider, artistId, canonicalArtistName, token, lookupNames),
                cancellationToken));
            if (!string.IsNullOrWhiteSpace(biography))
            {
                biographies.Add((provider, biography!));
            }
        }

        if (includePopularSongs
            && selectedProvider.HasValue
            && selectedProvider.Value != BiographyProvider.Spotify
            && !gate.IsUnavailable(ProviderName(BiographyProvider.Spotify)))
        {
            await gate.RunAsync(
                ProviderName(BiographyProvider.Spotify),
                token => _spotify.GetArtistPageAsync(
                    artistId,
                    canonicalArtistName,
                    forceRefresh: false,
                    forceRematch: false,
                    token,
                    includeDeezerLinking: false,
                    includeDiscography: false),
                cancellationToken);
        }

        foreach (var biography in biographies)
        {
            await _repository.UpsertArtistBiographyCacheAsync(
                artistId,
                ProviderName(biography.Provider),
                biography.Biography,
                selected: false,
                cancellationToken);
        }

        await _repository.SelectArtistBiographySourceAsync(
            artistId,
            selectedProvider.HasValue ? ProviderName(selectedProvider.Value) : null,
            cancellationToken);

        if (includeDiscography)
        {
            await RefreshMediaExtrasAsync(AppleSource, artistId, canonicalArtistName, _mediaExtras.RefreshAppleAsync, gate, cancellationToken);
            await RefreshMediaExtrasAsync(TidalSource, artistId, canonicalArtistName, _mediaExtras.RefreshTidalAsync, gate, cancellationToken);
        }

        return true;
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            await gate.RunAsync(
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Discovery is best-effort: the refresh continues with whatever identities are
            // already stored rather than failing the artist.
            _logger.LogWarning(ex, "Alias Spotify identity discovery failed for artist {ArtistId}.", artistId);
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
                BiographyProvider.LastFm => await ResolveLastFmBiographyAsync(artistName, lookupNames, cancellationToken),
                BiographyProvider.Audiomack => await _audiomack.ResolveBiographyAsync(artistId, artistName, cancellationToken, lookupNames),
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
            return null;
        }
    }

    private static IReadOnlyList<string> EffectiveLookupNames(string artistName, IReadOnlyList<string>? lookupNames)
        => lookupNames is { Count: > 0 } ? lookupNames : [artistName];

    /// <summary>
    /// Last.fm identifies an artist by name only. Each candidate name is attempted through the
    /// unchanged <see cref="LastFmArtistImageService.GetArtistBiographyAsync"/>, so the strict
    /// returned-name validation still runs per attempt; the alias simply gets its own attempt
    /// once the canonical name is genuinely rejected.
    /// </summary>
    private async Task<string?> ResolveLastFmBiographyAsync(
        string artistName,
        IReadOnlyList<string>? lookupNames,
        CancellationToken cancellationToken)
    {
        foreach (var name in EffectiveLookupNames(artistName, lookupNames))
        {
            var biography = (await _lastFm.GetArtistBiographyAsync(name, cancellationToken))?.Biography;
            if (!string.IsNullOrWhiteSpace(biography))
            {
                return biography;
            }
        }

        return null;
    }

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
            forceRefresh: false,
            forceRematch: false,
            cancellationToken,
            includeDeezerLinking: false,
            includeDiscography: false);
        var biography = page?.Artist?.Biography;
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

    private async Task RefreshMediaExtrasAsync(
        string provider,
        long artistId,
        string artistName,
        Func<long, string, CancellationToken, Task> refresh,
        ArtistMetadataProviderGate gate,
        CancellationToken cancellationToken)
    {
        try
        {
            await gate.RunAsync(
                provider,
                async token =>
                {
                    await refresh(artistId, artistName, token);
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
            _logger.LogWarning(ex, "Artist {Provider} media extras cache refresh failed for artist {ArtistId}.", provider, artistId);
        }
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
                allowArtistPageScrape: false);
        }
        else
        {
            // Discovery only. Each candidate name keeps the unchanged exact-name/title validation,
            // and the first verified Apple id wins; later refreshes take the id path above.
            var tracks = await _repository.GetArtistTrackTitlesAsync(artistId, 8, cancellationToken);
            result = null;
            foreach (var name in EffectiveLookupNames(artistName, lookupNames))
            {
                var attempt = await _apple.ResolveByExactArtistNameAndTracksAsync(name, tracks, cancellationToken);
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
        if (!int.TryParse(sourceId, out var qobuzId) || qobuzId <= 0)
        {
            return null;
        }

        var artist = await _qobuz.GetArtistAsync(qobuzId, "us-en", cancellationToken);
        return FirstNonEmpty(artist?.Biography?.Content, artist?.Biography?.Summary);
    }

    private async Task<string?> ResolveTidalBiographyAsync(long artistId, CancellationToken cancellationToken)
    {
        var sourceId = await _repository.GetArtistSourceIdAsync(artistId, TidalSource, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

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
                    "Tidal biography lookup rejected with HTTP {StatusCode} for artist {ArtistId}; check Tidal credentials/openapi access.",
                    (int)response.StatusCode,
                    artistId);
            }

            return null;
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
    bool? OcrTextArtBlockingEnabled = null);
public sealed record ArtistMetadataCacheRefreshResult(int Total, int Succeeded, int Failed, string? Error);
public sealed record ArtistMetadataOperationProgress(int Processed, int Total, string? CurrentArtist, long? CompletedArtistId = null, int Succeeded = 0, int Failed = 0);
