using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Identity;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Matching;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Services;

public sealed class TrackIdentityResolver : ITrackIdentityResolver
{
    private const string Spotify = "spotify";
    private const string Deezer = "deezer";
    private const string Apple = "apple";
    private const string Qobuz = "qobuz";
    private const string Tidal = "tidal";
    private const string Amazon = "amazon";
    private const string DefaultStorefront = "us";
    private const string DefaultLanguage = "en-US";
    private static readonly TimeSpan ProviderResolveTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AmazonResolveTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan AppleResolveTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AppleIsrcResolveTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SpotifyResolveTimeout = TimeSpan.FromSeconds(10);
    private static readonly MemoryCache AppleIdentityCache = new(new MemoryCacheOptions { SizeLimit = 512 });

    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly TidalDownloadService _tidalDownloadService;
    private readonly QobuzTrackResolver _qobuzTrackResolver;
    private readonly AmazonMusicMetadataService _amazonMusicMetadataService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly DeezerClient _deezerClient;
    private readonly ILogger<TrackIdentityResolver> _logger;

    public TrackIdentityResolver(
        ISpotifyIdResolver spotifyIdResolver,
        SpotifyMetadataService spotifyMetadataService,
        TidalDownloadService tidalDownloadService,
        QobuzTrackResolver qobuzTrackResolver,
        AmazonMusicMetadataService amazonMusicMetadataService,
        AppleMusicCatalogService appleCatalogService,
        AuthenticatedDeezerService authenticatedDeezerService,
        DeezerClient deezerClient,
        ILogger<TrackIdentityResolver> logger)
    {
        _spotifyIdResolver = spotifyIdResolver;
        _spotifyMetadataService = spotifyMetadataService;
        _tidalDownloadService = tidalDownloadService;
        _qobuzTrackResolver = qobuzTrackResolver;
        _amazonMusicMetadataService = amazonMusicMetadataService;
        _appleCatalogService = appleCatalogService;
        _authenticatedDeezerService = authenticatedDeezerService;
        _deezerClient = deezerClient;
        _logger = logger;
    }

    public async Task<TrackIdentityResolution> ResolveAsync(
        TrackIdentityResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var state = IdentityState.FromRequest(request);
        SeedDirectIdentities(state, request);
        await HydrateSourceMetadataAsync(state, request, cancellationToken);

        var targets = ResolveTargets(request);
        var candidates = new List<PlatformIdentityCandidate>();
        var resolutionTasks = new List<Task<List<PlatformIdentityCandidate>>>();

        if (ShouldResolve(targets, Spotify) && string.IsNullOrWhiteSpace(state.SpotifyId))
        {
            resolutionTasks.Add(RunResolverAsync((local, token) => ResolveSpotifyAsync(state, local, token), cancellationToken));
        }

        if (ShouldResolve(targets, Deezer) && string.IsNullOrWhiteSpace(state.DeezerId))
        {
            resolutionTasks.Add(RunResolverAsync((local, token) => ResolveDeezerAsync(state, local, token), cancellationToken));
        }

        if (ShouldResolve(targets, Apple) && string.IsNullOrWhiteSpace(state.AppleId))
        {
            resolutionTasks.Add(RunResolverAsync((local, token) => ResolveAppleAsync(state, request, local, token), cancellationToken));
        }

        if (ShouldResolve(targets, Qobuz) && string.IsNullOrWhiteSpace(state.QobuzId))
        {
            resolutionTasks.Add(RunResolverAsync((local, token) => ResolveQobuzAsync(state, local, token), cancellationToken));
        }

        if (ShouldResolve(targets, Tidal) && string.IsNullOrWhiteSpace(state.TidalId))
        {
            resolutionTasks.Add(RunResolverAsync((local, token) => ResolveTidalAsync(state, local, token), cancellationToken));
        }

        if (ShouldResolve(targets, Amazon) && string.IsNullOrWhiteSpace(state.AmazonId))
        {
            resolutionTasks.Add(RunResolverAsync((local, token) => ResolveAmazonAsync(state, local, token), cancellationToken));
        }

        var results = await Task.WhenAll(resolutionTasks);
        foreach (var result in results)
        {
            candidates.AddRange(result);
        }

        return state.ToResolution(candidates);
    }

    private static async Task<List<PlatformIdentityCandidate>> RunResolverAsync(
        Func<List<PlatformIdentityCandidate>, CancellationToken, Task> resolver,
        CancellationToken cancellationToken)
    {
        var candidates = new List<PlatformIdentityCandidate>();
        await resolver(candidates, cancellationToken);
        return candidates;
    }

    private static HashSet<string> ResolveTargets(TrackIdentityResolutionRequest request)
    {
        if (request.TargetPlatforms is not { Count: > 0 })
        {
            return new HashSet<string>([Spotify, Deezer, Apple, Qobuz, Tidal, Amazon], StringComparer.OrdinalIgnoreCase);
        }

        return request.TargetPlatforms
            .Where(static platform => !string.IsNullOrWhiteSpace(platform))
            .Select(static platform => platform.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldResolve(HashSet<string> targets, string platform)
        => targets.Contains(platform);

    private static void SeedDirectIdentities(IdentityState state, TrackIdentityResolutionRequest request)
    {
        var sourceUrl = request.SourceUrl;
        state.SpotifyId ??= EngineLinkParser.TryExtractSpotifyTrackId(sourceUrl, EngineLinkParser.RegexTimeout);
        state.DeezerId ??= EngineLinkParser.TryExtractDeezerTrackId(sourceUrl);
        state.AppleId ??= AppleIdParser.Resolve(null, sourceUrl);
        state.QobuzId ??= EngineLinkParser.TryExtractQobuzTrackId(sourceUrl);
        state.TidalId ??= EngineLinkParser.TryExtractTidalTrackId(sourceUrl);
        state.AmazonId ??= EngineLinkParser.TryExtractAmazonTrackId(sourceUrl, EngineLinkParser.RegexTimeout);
    }

    private async Task HydrateSourceMetadataAsync(
        IdentityState state,
        TrackIdentityResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var source = NormalizePlatform(request.SourcePlatform);
        if ((source == Spotify || !string.IsNullOrWhiteSpace(state.SpotifyId))
            && (string.IsNullOrWhiteSpace(state.Isrc)
                || string.IsNullOrWhiteSpace(state.Title)
                || string.IsNullOrWhiteSpace(state.Artist)))
        {
            await HydrateSpotifyMetadataAsync(state, request.SourceUrl, cancellationToken);
        }

        if ((source == Tidal || !string.IsNullOrWhiteSpace(state.TidalId))
            && (string.IsNullOrWhiteSpace(state.Isrc)
                || string.IsNullOrWhiteSpace(state.Title)
                || string.IsNullOrWhiteSpace(state.Artist)))
        {
            await HydrateTidalMetadataAsync(state, request.SourceUrl, cancellationToken);
        }

        if ((source == Deezer || !string.IsNullOrWhiteSpace(state.DeezerId))
            && string.IsNullOrWhiteSpace(state.Isrc)
            && !string.IsNullOrWhiteSpace(state.DeezerId))
        {
            await HydrateDeezerIsrcAsync(state, cancellationToken);
        }
    }

    private async Task HydrateSpotifyMetadataAsync(
        IdentityState state,
        string? sourceUrl,
        CancellationToken cancellationToken)
    {
        var spotifyUrl = FirstNonEmpty(sourceUrl, state.SpotifyUrl, BuildSpotifyUrl(state.SpotifyId));
        if (string.IsNullOrWhiteSpace(spotifyUrl))
        {
            return;
        }

        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(SpotifyResolveTimeout);
            var metadata = await _spotifyMetadataService.FetchByUrlAsync(spotifyUrl, providerTimeout.Token)
                .WaitAsync(providerTimeout.Token);
            var track = metadata?.TrackList.FirstOrDefault();
            if (track == null)
            {
                return;
            }

            state.SpotifyId ??= track.Id;
            state.SpotifyUrl ??= track.SourceUrl;
            state.Title ??= track.Name;
            state.Artist ??= track.Artists;
            state.Album ??= track.Album;
            state.Isrc ??= track.Isrc;
            state.DurationMs ??= track.DurationMs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed hydrating Spotify metadata.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out hydrating Spotify metadata.");
        }
    }

    private async Task HydrateTidalMetadataAsync(IdentityState state, string? sourceUrl, CancellationToken cancellationToken)
    {
        var tidalInput = FirstNonEmpty(state.TidalId, sourceUrl, state.TidalUrl);
        if (string.IsNullOrWhiteSpace(tidalInput))
        {
            return;
        }

        try
        {
            var track = await _tidalDownloadService.ResolveTrackMetadataAsync(tidalInput, cancellationToken);
            if (track == null)
            {
                return;
            }

            state.TidalId ??= track.Id.ToString(CultureInfo.InvariantCulture);
            state.TidalUrl ??= track.Url;
            state.Title ??= track.Title;
            state.Artist ??= track.Artist;
            state.Album ??= track.Album;
            state.Isrc ??= track.Isrc;
            state.DurationMs ??= track.DurationSeconds > 0 ? track.DurationSeconds * 1000 : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed hydrating Tidal metadata.");
        }
    }

    private async Task HydrateDeezerIsrcAsync(IdentityState state, CancellationToken cancellationToken)
    {
        try
        {
            if (!long.TryParse(state.DeezerId, NumberStyles.None, CultureInfo.InvariantCulture, out var deezerId))
            {
                return;
            }

            if (!await _authenticatedDeezerService.EnsureAuthenticatedAsync())
            {
                return;
            }

            var track = await _deezerClient.GetTrackAsync(deezerId.ToString(CultureInfo.InvariantCulture))
                .WaitAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(track?.Isrc))
            {
                state.Isrc = track.Isrc.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed hydrating Deezer ISRC.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out hydrating Deezer ISRC.");
        }
    }

    private async Task ResolveSpotifyAsync(
        IdentityState state,
        List<PlatformIdentityCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (!HasSearchSignal(state))
        {
            return;
        }

        string? id;
        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(SpotifyResolveTimeout);
            var providerToken = providerTimeout.Token;
            id = await _spotifyIdResolver.ResolveTrackIdAsync(
                state.Title ?? string.Empty,
                state.Artist ?? string.Empty,
                state.Album,
                state.Isrc,
                providerToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out resolving Spotify identity.");
            candidates.Add(Rejected(Spotify, "spotify-timeout"));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed resolving Spotify identity.");
            candidates.Add(Rejected(Spotify, "spotify-error"));
            return;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            candidates.Add(Rejected(Spotify, "spotify-unresolved"));
            return;
        }

        state.SpotifyId = id.Trim();
        state.SpotifyUrl = BuildSpotifyUrl(state.SpotifyId);
        candidates.Add(Accepted(Spotify, state.SpotifyId, state.SpotifyUrl, "spotify-resolver"));
    }

    private async Task ResolveDeezerAsync(
        IdentityState state,
        List<PlatformIdentityCandidate> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _authenticatedDeezerService.EnsureAuthenticatedAsync())
            {
                candidates.Add(Rejected(Deezer, "deezer-auth-missing"));
                return;
            }

            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(ProviderResolveTimeout);
            var providerToken = providerTimeout.Token;
            string? id = null;
            if (!string.IsNullOrWhiteSpace(state.Isrc))
            {
                var track = await _deezerClient.GetTrackByIsrcAsync(state.Isrc)
                    .WaitAsync(providerToken);
                var validation = track == null
                    ? null
                    : ValidateDeezerCandidate(state, track.Id, track.Isrc, track.TitleShort, track.Artist?.Name, track.Album?.Title, track.Duration);
                if (validation?.Accepted == true)
                {
                    id = track!.Id;
                }
            }

            if (string.IsNullOrWhiteSpace(id)
                && !string.IsNullOrWhiteSpace(state.Title)
                && !string.IsNullOrWhiteSpace(state.Artist))
            {
                id = await ResolveDeezerByMetadataAsync(state, providerToken);
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                candidates.Add(Rejected(Deezer, "deezer-unresolved"));
                return;
            }

            state.DeezerId = id;
            state.DeezerUrl = BuildDeezerUrl(id);
            candidates.Add(Accepted(Deezer, state.DeezerId, state.DeezerUrl, "deezer-native"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed resolving Deezer identity.");
            candidates.Add(Rejected(Deezer, "deezer-error"));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out resolving Deezer identity.");
            candidates.Add(Rejected(Deezer, "deezer-timeout"));
        }
    }

    private async Task<string?> ResolveDeezerByMetadataAsync(IdentityState state, CancellationToken cancellationToken)
    {
        var query = string.Join(' ', new[] { state.Artist, state.Title, state.Album }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        var tracks = await _deezerClient.SearchTracksAsync(query, 25, cancellationToken)
            .WaitAsync(cancellationToken);
        string? bestId = null;
        var bestScore = double.MinValue;
        foreach (var track in tracks.Take(15))
        {
            var candidate = await HydrateDeezerCandidateAsync(track, cancellationToken);
            var validation = ValidateDeezerCandidate(
                state,
                candidate.Id,
                candidate.Isrc,
                candidate.Title,
                candidate.Artist,
                candidate.Album,
                candidate.DurationSeconds);
            if (validation.Accepted && validation.Score > bestScore)
            {
                bestScore = validation.Score;
                bestId = candidate.Id;
            }
        }

        return bestId;
    }

    private async Task<DeezerIdentityCandidate> HydrateDeezerCandidateAsync(
        Track track,
        CancellationToken cancellationToken)
    {
        var id = track.Id;
        var title = track.Title;
        var artist = track.Artist?.Name ?? track.MainArtist?.Name;
        var album = track.Album?.Title;
        var isrc = FirstNonEmpty(track.Isrc, track.ISRC);
        var durationSeconds = track.Duration;

        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                var full = await _deezerClient.GetTrackAsync(id)
                    .WaitAsync(cancellationToken);
                title = FirstNonEmpty(full.TitleShort, full.Title, title);
                artist = FirstNonEmpty(full.Artist?.Name, artist);
                album = FirstNonEmpty(full.Album?.Title, album);
                isrc = FirstNonEmpty(full.Isrc, isrc);
                durationSeconds = full.Duration > 0 ? full.Duration : durationSeconds;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Central identity resolver failed hydrating Deezer search candidate {TrackId}.", id);
            }
        }

        return new DeezerIdentityCandidate(id, isrc, title, artist, album, durationSeconds);
    }

    private static TrackCandidateValidationResult ValidateDeezerCandidate(
        IdentityState state,
        string? id,
        string? isrc,
        string? title,
        string? artist,
        string? album,
        int durationSeconds)
    {
        return TrackCandidateValidator.Validate(
            new TrackMatchSource(state.Isrc, state.Title, state.Artist, state.Album, state.DurationMs),
            new TrackMatchCandidate(
                id,
                isrc,
                title,
                artist,
                album,
                durationSeconds > 0 ? durationSeconds * 1000 : null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 8_000));
    }

    private sealed record DeezerIdentityCandidate(
        string? Id,
        string? Isrc,
        string? Title,
        string? Artist,
        string? Album,
        int DurationSeconds);

    private async Task ResolveAppleAsync(
        IdentityState state,
        TrackIdentityResolutionRequest request,
        List<PlatformIdentityCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var hasConfiguredStorefront = !string.IsNullOrWhiteSpace(request.Storefront);
        var storefront = hasConfiguredStorefront ? request.Storefront!.Trim() : DefaultStorefront;
        var language = string.IsNullOrWhiteSpace(request.Language) ? DefaultLanguage : request.Language.Trim();
        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(AppleResolveTimeout);
            var providerToken = providerTimeout.Token;
            if (!hasConfiguredStorefront)
            {
                storefront = await _appleCatalogService.ResolveStorefrontAsync(storefront, request.MediaUserToken, providerToken)
                    .WaitAsync(providerToken);
            }
            var cacheKey = BuildAppleIdentityCacheKey(state, storefront, language);
            if (!string.IsNullOrWhiteSpace(cacheKey)
                && AppleIdentityCache.TryGetValue(cacheKey, out AppleIdentityCandidate? cachedCandidate)
                && cachedCandidate != null)
            {
                ApplyAppleCandidate(state, cachedCandidate, storefront);
                candidates.Add(Accepted(Apple, state.AppleId, state.AppleUrl, "apple-catalog-cache"));
                return;
            }

            AppleIdentityCandidate? appleCandidate = null;
            if (HasAppleMetadataSearchSignal(state))
            {
                appleCandidate = await ResolveAppleCandidateBySearchAsync(state, storefront, language, providerToken);
            }

            appleCandidate ??= await TryResolveAppleCandidateByIsrcAsync(state, storefront, language, request.MediaUserToken, providerToken);
            if (appleCandidate == null || ShouldSearchForExactAppleAlbum(state.Album, appleCandidate.AlbumName))
            {
                var searchCandidate = await ResolveAppleCandidateBySearchAsync(state, storefront, language, providerToken);
                if (searchCandidate != null
                    && (!ShouldSearchForExactAppleAlbum(state.Album, searchCandidate.AlbumName)
                        || appleCandidate == null))
                {
                    appleCandidate = searchCandidate;
                }
            }
            if (appleCandidate == null || string.IsNullOrWhiteSpace(appleCandidate.Id))
            {
                candidates.Add(Rejected(Apple, "apple-unresolved"));
                return;
            }

            ApplyAppleCandidate(state, appleCandidate, storefront);
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                AppleIdentityCache.Set(cacheKey, appleCandidate, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                    Size = 1
                });
            }
            candidates.Add(Accepted(Apple, state.AppleId, state.AppleUrl, "apple-catalog"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed resolving Apple identity.");
            candidates.Add(Rejected(Apple, "apple-error"));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out resolving Apple identity.");
            candidates.Add(Rejected(Apple, "apple-timeout"));
        }
    }

    private async Task<AppleIdentityCandidate?> ResolveAppleCandidateByIsrcAsync(
        IdentityState state,
        string storefront,
        string language,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Isrc))
        {
            return null;
        }

        using var doc = await _appleCatalogService.GetSongByIsrcAsync(
            state.Isrc,
            storefront,
            language,
            cancellationToken,
            mediaUserToken);
        var candidate = FindBestAppleCandidateFromData(doc.RootElement, state);
        return candidate == null
            ? null
            : await HydrateAppleCandidateAsync(candidate, state, storefront, language, mediaUserToken, cancellationToken);
    }

    private async Task<AppleIdentityCandidate?> TryResolveAppleCandidateByIsrcAsync(
        IdentityState state,
        string storefront,
        string language,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Isrc))
        {
            return null;
        }

        try
        {
            using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lookupTimeout.CancelAfter(AppleIsrcResolveTimeout);
            return await ResolveAppleCandidateByIsrcAsync(
                state,
                storefront,
                language,
                mediaUserToken,
                lookupTimeout.Token).WaitAsync(lookupTimeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver Apple ISRC lookup did not complete quickly; falling back to Apple search.");
            return null;
        }
    }

    private static bool HasAppleMetadataSearchSignal(IdentityState state)
        => !string.IsNullOrWhiteSpace(state.Title)
           && !string.IsNullOrWhiteSpace(state.Artist)
           && !string.IsNullOrWhiteSpace(state.Album);

    private static void ApplyAppleCandidate(IdentityState state, AppleIdentityCandidate appleCandidate, string storefront)
    {
        state.AppleId = appleCandidate.Id.Trim();
        state.AppleUrl = BuildAppleUrl(state.AppleId, storefront);
        state.AppleAlbumId = appleCandidate.AlbumId;
        state.AppleAlbumName = appleCandidate.AlbumName;
        state.AppleArtistName = appleCandidate.ArtistName;
        state.AppleIsrc = appleCandidate.Isrc;
        state.AppleDurationMs = appleCandidate.DurationMs;
    }

    private static string? BuildAppleIdentityCacheKey(IdentityState state, string storefront, string language)
    {
        var title = TrackTitleMatcher.NormalizeText(state.Title);
        var artist = TrackTitleMatcher.NormalizeText(state.Artist);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var album = TrackTitleMatcher.NormalizeText(state.Album);
        var isrc = string.IsNullOrWhiteSpace(state.Isrc) ? string.Empty : state.Isrc.Trim().ToUpperInvariant();
        var durationBucket = state.DurationMs is > 0
            ? (state.DurationMs.Value / 1000).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        return $"apple:identity:{storefront}:{language}:{title}:{artist}:{album}:{isrc}:{durationBucket}";
    }

    private async Task<AppleIdentityCandidate?> ResolveAppleCandidateBySearchAsync(
        IdentityState state,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Title) || string.IsNullOrWhiteSpace(state.Artist))
        {
            return null;
        }

        var term = string.Join(' ', new[] { state.Title, state.Artist, state.Album }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        using var doc = await _appleCatalogService.SearchAsync(
            term,
            limit: 25,
            storefront: storefront,
            language: language,
            cancellationToken,
            new AppleMusicCatalogService.AppleSearchOptions(
                TypesOverride: "songs,albums",
                IncludeRelationshipsTracks: false));

        var candidate = FindBestAppleCandidateFromSearch(doc.RootElement, state);
        return candidate == null
            ? null
            : await HydrateAppleCandidateAsync(candidate, state, storefront, language, null, cancellationToken);
    }

    private async Task<AppleIdentityCandidate> HydrateAppleCandidateAsync(
        AppleIdentityCandidate candidate,
        IdentityState state,
        string storefront,
        string language,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.Id))
        {
            return candidate;
        }

        try
        {
            using var hydrateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hydrateTimeout.CancelAfter(AppleIsrcResolveTimeout);
            using var doc = await _appleCatalogService.GetSongAsync(
                candidate.Id,
                storefront,
                language,
                hydrateTimeout.Token,
                mediaUserToken).WaitAsync(hydrateTimeout.Token);
            var hydrated = FindBestAppleCandidateFromData(doc.RootElement, state);
            return hydrated == null
                ? candidate
                : candidate with
                {
                    AlbumId = FirstNonEmpty(hydrated.AlbumId, candidate.AlbumId),
                    AlbumName = FirstNonEmpty(hydrated.AlbumName, candidate.AlbumName),
                    ArtistName = FirstNonEmpty(hydrated.ArtistName, candidate.ArtistName),
                    Isrc = FirstNonEmpty(hydrated.Isrc, candidate.Isrc),
                    DurationMs = hydrated.DurationMs ?? candidate.DurationMs
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed hydrating Apple song identity {AppleId}.", candidate.Id);
            return candidate;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out hydrating Apple song identity {AppleId}; using search identity.", candidate.Id);
            return candidate;
        }
    }

    private static AppleIdentityCandidate? FindBestAppleCandidateFromSearch(JsonElement root, IdentityState state)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Object
            || !results.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Object
            || !songs.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return FindBestAppleCandidate(data, state, allowVariantIsrc: true);
    }

    private static AppleIdentityCandidate? FindBestAppleCandidateFromData(JsonElement root, IdentityState state)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return FindBestAppleCandidate(data, state, allowVariantIsrc: false);
    }

    private static AppleIdentityCandidate? FindBestAppleCandidate(JsonElement data, IdentityState state, bool allowVariantIsrc)
    {
        AppleIdentityCandidate? bestCandidate = null;
        var bestScore = double.MinValue;
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)
                || !item.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var candidate = ReadAppleCandidate(item, attributes, id);
            var source = new TrackMatchSource(
                state.Isrc,
                state.Title,
                state.Artist,
                state.Album,
                state.DurationMs);
            var matchCandidate = new TrackMatchCandidate(
                candidate.Id,
                candidate.Isrc,
                candidate.Title,
                candidate.ArtistName,
                candidate.AlbumName,
                candidate.DurationMs);
            var validation = TrackCandidateValidator.Validate(
                source,
                matchCandidate,
                new TrackCandidateValidationOptions(
                    StrictWithoutIsrc: true,
                    AllowMissingCandidateArtist: false,
                    RequireCandidateDurationWhenSourceHasDuration: false,
                    MaxIsrcDurationDifferenceMs: 20_000,
                    MaxMetadataDurationDifferenceMs: 8_000));
            if (!validation.Accepted && allowVariantIsrc && string.Equals(validation.Reason, "isrc_mismatch", StringComparison.OrdinalIgnoreCase))
            {
                validation = TrackCandidateValidator.Validate(
                    source with { Isrc = null },
                    matchCandidate with { Isrc = null },
                    new TrackCandidateValidationOptions(
                        StrictWithoutIsrc: true,
                        AllowMissingCandidateArtist: false,
                        RequireCandidateDurationWhenSourceHasDuration: true,
                        MaxMetadataDurationDifferenceMs: 8_000));
            }
            var score = validation.Accepted
                ? ApplyAppleAlbumSelectionWeight(validation.Score, state.Album, candidate.AlbumName)
                : validation.Score;
            if (validation.Accepted && score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate with { Score = score };
            }
        }

        return bestCandidate;
    }

    private static double ApplyAppleAlbumSelectionWeight(double score, string? sourceAlbum, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(sourceAlbum) || string.IsNullOrWhiteSpace(candidateAlbum))
        {
            return score;
        }

        var source = NormalizeAppleAlbumForExactMatch(sourceAlbum);
        var candidate = NormalizeAppleAlbumForExactMatch(candidateAlbum);
        if (string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return score + 50d;
        }

        return score - 25d;
    }

    private static bool ShouldSearchForExactAppleAlbum(string? sourceAlbum, string? candidateAlbum)
        => !string.IsNullOrWhiteSpace(sourceAlbum)
           && !string.IsNullOrWhiteSpace(candidateAlbum)
           && !string.Equals(
               NormalizeAppleAlbumForExactMatch(sourceAlbum),
               NormalizeAppleAlbumForExactMatch(candidateAlbum),
               StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAppleAlbumForExactMatch(string value)
        => TrackTitleMatcher.RemoveAtmosVersionMarker(value).Trim();

    private static AppleIdentityCandidate ReadAppleCandidate(JsonElement item, JsonElement attributes, string id)
    {
        return new AppleIdentityCandidate(
            Id: id,
            Title: ReadString(attributes, "name"),
            ArtistName: ReadString(attributes, "artistName"),
            AlbumName: ReadString(attributes, "albumName"),
            Isrc: ReadString(attributes, "isrc"),
            DurationMs: ReadInt(attributes, "durationInMillis"),
            AlbumId: TryExtractAppleAlbumId(item),
            Score: 0d);
    }

    private static string? TryExtractAppleAlbumId(JsonElement item)
    {
        if (!item.TryGetProperty("relationships", out var relationships)
            || relationships.ValueKind != JsonValueKind.Object
            || !relationships.TryGetProperty("albums", out var albums)
            || albums.ValueKind != JsonValueKind.Object
            || !albums.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return null;
        }

        return data[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    private sealed record AppleIdentityCandidate(
        string Id,
        string? Title,
        string? ArtistName,
        string? AlbumName,
        string? Isrc,
        int? DurationMs,
        string? AlbumId,
        double Score);

    private async Task ResolveQobuzAsync(
        IdentityState state,
        List<PlatformIdentityCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (!HasSearchSignal(state))
        {
            return;
        }

        QobuzTrackResolution? resolved;
        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(ProviderResolveTimeout);
            var providerToken = providerTimeout.Token;
            resolved = await _qobuzTrackResolver.ResolveTrackAsync(
                state.Isrc,
                state.Title,
                state.Artist,
                state.Album,
                state.DurationMs,
                providerToken).WaitAsync(providerToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out resolving Qobuz identity.");
            candidates.Add(Rejected(Qobuz, "qobuz-timeout"));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed resolving Qobuz identity.");
            candidates.Add(Rejected(Qobuz, "qobuz-error"));
            return;
        }

        if (resolved?.Track.Id is not > 0)
        {
            candidates.Add(Rejected(Qobuz, "qobuz-unresolved"));
            return;
        }

        state.QobuzId = resolved.Track.Id.ToString(CultureInfo.InvariantCulture);
        state.QobuzUrl = BuildQobuzUrl(state.QobuzId);
        state.Isrc ??= resolved.Track.ISRC;
        state.Title ??= resolved.Track.Title;
        state.Album ??= resolved.Track.Album?.Title;
        candidates.Add(Accepted(Qobuz, state.QobuzId, state.QobuzUrl, resolved.Source, resolved.Score));
    }

    private async Task ResolveTidalAsync(
        IdentityState state,
        List<PlatformIdentityCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Title) || string.IsNullOrWhiteSpace(state.Artist))
        {
            return;
        }

        var durationSeconds = state.DurationMs is > 0
            ? Math.Max(1, (int)Math.Round(state.DurationMs.Value / 1000d))
            : 0;
        string? tidalUrl;
        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(ProviderResolveTimeout);
            var providerToken = providerTimeout.Token;
            tidalUrl = await _tidalDownloadService.ResolveTrackUrlAsync(
                state.Title,
                state.Artist,
                state.Isrc ?? string.Empty,
                durationSeconds,
                providerToken).WaitAsync(providerToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out resolving Tidal identity.");
            candidates.Add(Rejected(Tidal, "tidal-timeout"));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed resolving Tidal identity.");
            candidates.Add(Rejected(Tidal, "tidal-error"));
            return;
        }

        if (string.IsNullOrWhiteSpace(tidalUrl))
        {
            candidates.Add(Rejected(Tidal, "tidal-unresolved"));
            return;
        }

        state.TidalUrl = tidalUrl;
        state.TidalId = EngineLinkParser.TryExtractTidalTrackId(tidalUrl);
        candidates.Add(Accepted(Tidal, state.TidalId, state.TidalUrl, "tidal-native"));
    }

    private async Task ResolveAmazonAsync(
        IdentityState state,
        List<PlatformIdentityCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Title) || string.IsNullOrWhiteSpace(state.Artist))
        {
            return;
        }

        AmazonCatalogItem? resolved;
        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(AmazonResolveTimeout);
            var providerToken = providerTimeout.Token;
            resolved = await _amazonMusicMetadataService.ResolveTrackAsync(
                state.Title,
                state.Artist,
                state.Album,
                state.DurationMs,
                state.Isrc,
                providerToken).WaitAsync(providerToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Central identity resolver timed out resolving Amazon identity.");
            candidates.Add(Rejected(Amazon, "amazon-timeout"));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Central identity resolver failed resolving Amazon identity.");
            candidates.Add(Rejected(Amazon, "amazon-error"));
            return;
        }

        if (resolved == null || string.IsNullOrWhiteSpace(resolved.Id))
        {
            candidates.Add(Rejected(Amazon, "amazon-unresolved"));
            return;
        }

        state.AmazonId = resolved.Id;
        state.AmazonUrl = resolved.Url;
        state.Isrc ??= resolved.Isrc;
        candidates.Add(Accepted(Amazon, state.AmazonId, state.AmazonUrl, "amazon-native"));
    }

    private static PlatformIdentityCandidate Accepted(
        string platform,
        string? id,
        string? url,
        string source,
        double score = 1d)
        => new(platform, id, url, source, true, null, score);

    private static PlatformIdentityCandidate Rejected(string platform, string reason)
        => new(platform, null, null, "central", false, reason);

    private static bool HasSearchSignal(IdentityState state)
        => !string.IsNullOrWhiteSpace(state.Isrc)
           || (!string.IsNullOrWhiteSpace(state.Title) && !string.IsNullOrWhiteSpace(state.Artist));

    private static string? NormalizePlatform(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeDeezerTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return long.TryParse(normalized, out var numeric) && numeric > 0
            ? numeric.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;

    private static string? BuildSpotifyUrl(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : $"https://open.spotify.com/track/{Uri.EscapeDataString(id.Trim())}";

    private static string? BuildDeezerUrl(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : $"https://www.deezer.com/track/{Uri.EscapeDataString(id.Trim())}";

    private static string? BuildAppleUrl(string? id, string storefront)
        => string.IsNullOrWhiteSpace(id) ? null : $"https://music.apple.com/{storefront}/song/{Uri.EscapeDataString(id.Trim())}?i={Uri.EscapeDataString(id.Trim())}";

    private static string? BuildQobuzUrl(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : $"https://play.qobuz.com/track/{Uri.EscapeDataString(id.Trim())}";

    private sealed class IdentityState
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? DurationMs { get; set; }
        public string? SpotifyId { get; set; }
        public string? SpotifyUrl { get; set; }
        public string? DeezerId { get; set; }
        public string? DeezerUrl { get; set; }
        public string? AppleId { get; set; }
        public string? AppleUrl { get; set; }
        public string? AppleAlbumId { get; set; }
        public string? AppleAlbumName { get; set; }
        public string? AppleArtistName { get; set; }
        public string? AppleIsrc { get; set; }
        public int? AppleDurationMs { get; set; }
        public string? QobuzId { get; set; }
        public string? QobuzUrl { get; set; }
        public string? TidalId { get; set; }
        public string? TidalUrl { get; set; }
        public string? AmazonId { get; set; }
        public string? AmazonUrl { get; set; }

        public static IdentityState FromRequest(TrackIdentityResolutionRequest request)
            => new()
            {
                Title = EmptyToNull(request.Title),
                Artist = EmptyToNull(request.Artist),
                Album = EmptyToNull(request.Album),
                Isrc = EmptyToNull(request.Isrc),
                DurationMs = request.DurationMs is > 0 ? request.DurationMs : null,
                SpotifyId = EmptyToNull(request.SpotifyId),
                SpotifyUrl = BuildSpotifyUrl(request.SpotifyId),
                DeezerId = NormalizeDeezerTrackId(request.DeezerId),
                DeezerUrl = BuildDeezerUrl(request.DeezerId),
                AppleId = EmptyToNull(request.AppleId),
                AppleUrl = BuildAppleUrl(request.AppleId, DefaultStorefront),
                AppleAlbumName = EmptyToNull(request.Album),
                AppleIsrc = EmptyToNull(request.Isrc),
                AppleDurationMs = request.DurationMs is > 0 ? request.DurationMs : null,
                QobuzId = EmptyToNull(request.QobuzId),
                QobuzUrl = BuildQobuzUrl(request.QobuzId),
                TidalId = EmptyToNull(request.TidalId),
                TidalUrl = string.IsNullOrWhiteSpace(request.TidalId) ? null : $"https://tidal.com/browse/track/{request.TidalId.Trim()}",
                AmazonId = EngineLinkParser.NormalizeAmazonTrackId(request.AmazonId),
                AmazonUrl = string.IsNullOrWhiteSpace(request.AmazonId) ? null : $"https://music.amazon.com/tracks/{request.AmazonId.Trim()}"
            };

        public TrackIdentityResolution ToResolution(IReadOnlyList<PlatformIdentityCandidate> candidates)
            => new(
                Title,
                Artist,
                Album,
                Isrc,
                DurationMs,
                SpotifyId,
                SpotifyUrl ?? BuildSpotifyUrl(SpotifyId),
                DeezerId,
                DeezerUrl ?? BuildDeezerUrl(DeezerId),
                AppleId,
                AppleUrl ?? BuildAppleUrl(AppleId, DefaultStorefront),
                AppleAlbumId,
                AppleAlbumName,
                AppleArtistName,
                AppleIsrc,
                AppleDurationMs,
                QobuzId,
                QobuzUrl ?? BuildQobuzUrl(QobuzId),
                TidalId,
                TidalUrl ?? (string.IsNullOrWhiteSpace(TidalId) ? null : $"https://tidal.com/browse/track/{TidalId}"),
                AmazonId,
                AmazonUrl ?? (string.IsNullOrWhiteSpace(AmazonId) ? null : $"https://music.amazon.com/tracks/{AmazonId}"),
                candidates);

        private static string? EmptyToNull(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
