using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ApiPlaylist = DeezSpoTag.Core.Models.Deezer.ApiPlaylist;
using ApiTrack = DeezSpoTag.Core.Models.Deezer.ApiTrack;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistWatchService
{
    private const string SpotifySource = "spotify";
    private const string DeezerSource = "deezer";
    private const string SmartTracklistSource = "smarttracklist";
    private const string AppleSource = "apple";
    private const string BoomplaySource = "boomplay";
    private const string RecommendationsSource = "recommendations";
    private const string QueuedStatus = "queued";
    private const string AlbumField = "album";
    private const string SpotifyHomeTrendingSourceId = "home-trending-songs";
    private const string SpotifyTrendingSongsSectionUri = "spotify:section:0JQ5DB5E8N831KzFzsBBQ2";
    private static readonly string[] JsonStringObjectPropertyNames = ["standard", "short", "text"];
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly SpotifyPathfinderMetadataClient _spotifyPathfinderMetadataClient;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly DeezerClient _deezerClient;
    private readonly DeezerGatewayService _deezerGatewayService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly LibraryRecommendationService _libraryRecommendationService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly ILogger<PlaylistWatchService> _logger;

    public sealed class PlaylistWatchPlatformServices
    {
        public required SpotifyMetadataService SpotifyMetadataService { get; init; }
        public required SpotifyPathfinderMetadataClient SpotifyPathfinderMetadataClient { get; init; }
        public required SpotifyArtistService SpotifyArtistService { get; init; }
        public required DeezerClient DeezerClient { get; init; }
        public required DeezerGatewayService DeezerGatewayService { get; init; }
        public required AppleMusicCatalogService AppleCatalogService { get; init; }
        public required BoomplayMetadataService BoomplayMetadataService { get; init; }
        public required LibraryRecommendationService LibraryRecommendationService { get; init; }
    }

    public PlaylistWatchService(
        LibraryRepository libraryRepository,
        PlaylistWatchPlatformServices platformServices,
        DeezSpoTagSettingsService settingsService,
        IServiceProvider serviceProvider,
        PlaylistSyncService playlistSyncService,
        PlaylistVisualService playlistVisualService,
        ILogger<PlaylistWatchService> logger)
    {
        _libraryRepository = libraryRepository;
        _spotifyMetadataService = platformServices.SpotifyMetadataService;
        _spotifyPathfinderMetadataClient = platformServices.SpotifyPathfinderMetadataClient;
        _spotifyArtistService = platformServices.SpotifyArtistService;
        _deezerClient = platformServices.DeezerClient;
        _deezerGatewayService = platformServices.DeezerGatewayService;
        _appleCatalogService = platformServices.AppleCatalogService;
        _boomplayMetadataService = platformServices.BoomplayMetadataService;
        _libraryRecommendationService = platformServices.LibraryRecommendationService;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _playlistSyncService = playlistSyncService;
        _playlistVisualService = playlistVisualService;
        _logger = logger;
    }

    private Task UpsertPlaylistWatchStateAsync(
        LibraryRepository.PlaylistWatchStateUpsertInput state,
        CancellationToken cancellationToken)
    {
        return _libraryRepository.UpsertPlaylistWatchStateAsync(
            state,
            cancellationToken);
    }

    public async Task CheckWatchlistAsync(CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            _logger.LogDebug("Playlist watchlist skipped - library DB not configured.");
            return;
        }

        var playlists = await _libraryRepository.GetPlaylistWatchlistAsync(cancellationToken);
        if (playlists.Count == 0)
        {
            return;
        }

        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CheckPlaylistAsync(playlist, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Playlist watch failed for {Source}:{SourceId}", playlist.Source, playlist.SourceId);
            }
        }
    }

    public Task CheckPlaylistWatchItemAsync(PlaylistWatchlistDto playlist, CancellationToken cancellationToken)
    {
        if (playlist == null)
        {
            return Task.CompletedTask;
        }

        return CheckPlaylistAsync(playlist, cancellationToken);
    }

    public sealed record PlaylistTrackCandidate(
        string TrackSourceId,
        string? Isrc,
        string Title,
        string Artist,
        string Album,
        int? ReleaseYear,
        bool? Explicit,
        IReadOnlyList<string> Genres);

    private async Task<string?> ResolvePlaylistImageUrlAsync(
        string source,
        string sourceId,
        string? playlistName,
        string? imageUrl,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        return await _playlistVisualService.ResolveManagedVisualUrlAsync(
            source,
            sourceId,
            playlistName,
            imageUrl,
            preference?.ReuseSavedArtwork == true,
            cancellationToken);
    }

    public async Task<IReadOnlyList<PlaylistTrackCandidate>> GetPlaylistTrackCandidatesAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeWatchSource(source);
        var normalizedSourceId = (sourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(normalizedSourceId))
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        if (!_libraryRepository.IsConfigured)
        {
            return await FetchLivePlaylistTrackCandidatesAsync(normalizedSource, normalizedSourceId, cancellationToken);
        }

        var isMonitored = await _libraryRepository.IsPlaylistWatchlistedAsync(normalizedSource, normalizedSourceId, cancellationToken);
        if (!isMonitored)
        {
            return await FetchLivePlaylistTrackCandidatesAsync(normalizedSource, normalizedSourceId, cancellationToken);
        }

        var watchState = await _libraryRepository.GetPlaylistWatchStateAsync(normalizedSource, normalizedSourceId, cancellationToken);
        var currentSnapshotId = NormalizeSnapshotId(watchState?.SnapshotId);
        var cached = await _libraryRepository.GetPlaylistTrackCandidateCacheAsync(normalizedSource, normalizedSourceId, cancellationToken);
        if (cached is not null)
        {
            var cachedSnapshotId = NormalizeSnapshotId(cached.SnapshotId);
            if (string.Equals(cachedSnapshotId, currentSnapshotId, StringComparison.Ordinal))
            {
                var cachedCandidates = TryDeserializePlaylistTrackCandidates(cached.CandidatesJson);
                if (cachedCandidates is not null)
                {
                    return cachedCandidates;
                }

                _logger.LogWarning("Playlist candidate cache JSON invalid. Regenerating for Source:SourceId.");
            }
        }

        var freshCandidates = await FetchLivePlaylistTrackCandidatesAsync(normalizedSource, normalizedSourceId, cancellationToken);
        await _libraryRepository.UpsertPlaylistTrackCandidateCacheAsync(
            normalizedSource,
            normalizedSourceId,
            currentSnapshotId,
            JsonSerializer.Serialize(freshCandidates),
            cancellationToken);
        return freshCandidates;
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> FetchLivePlaylistTrackCandidatesAsync(
        string normalizedSource,
        string normalizedSourceId,
        CancellationToken cancellationToken)
    {
        return normalizedSource switch
        {
            SpotifySource => await GetSpotifyTrackCandidatesAsync(normalizedSourceId, cancellationToken),
            DeezerSource => await GetDeezerTrackCandidatesAsync(normalizedSourceId, cancellationToken),
            SmartTracklistSource => await GetSmartTracklistTrackCandidatesAsync(normalizedSourceId, cancellationToken),
            AppleSource => await GetAppleTrackCandidatesAsync(normalizedSourceId, cancellationToken),
            BoomplaySource => await GetBoomplayTrackCandidatesAsync(normalizedSourceId, cancellationToken),
            RecommendationsSource => await GetRecommendationTrackCandidatesAsync(normalizedSourceId, cancellationToken),
            _ => Array.Empty<PlaylistTrackCandidate>()
        };
    }

    private static IReadOnlyList<PlaylistTrackCandidate>? TryDeserializePlaylistTrackCandidates(string candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        try
        {
            var candidates = JsonSerializer.Deserialize<List<PlaylistTrackCandidate>>(candidatesJson);
            return candidates is null
                ? Array.Empty<PlaylistTrackCandidate>()
                : (IReadOnlyList<PlaylistTrackCandidate>)candidates;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeSnapshotId(string? snapshotId)
    {
        return string.IsNullOrWhiteSpace(snapshotId) ? null : snapshotId.Trim();
    }

    private async Task AddPlaylistWatchHistoryAsync(
        string source,
        string sourceId,
        string playlistName,
        int trackCount,
        string status,
        CancellationToken cancellationToken,
        string? artistName = null)
    {
        if (trackCount <= 0)
        {
            return;
        }

        await _libraryRepository.AddWatchlistHistoryAsync(
            new WatchlistHistoryInsert(
                source,
                "playlist",
                sourceId,
                playlistName,
                "playlist",
                trackCount,
                status,
                ArtistName: artistName),
            cancellationToken);
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetSpotifyTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>();

        if (IsSpotifyHomeTrendingSourceId(sourceId))
        {
            await AddSpotifyHomeTrendingCandidatesAsync(candidates, seen, cancellationToken);
            return candidates;
        }

        if (TryGetSpotifyArtistTopTracksSourceId(sourceId, out var artistId))
        {
            await AddSpotifyArtistTopTrackCandidatesAsync(candidates, seen, artistId, cancellationToken);
            return candidates;
        }

        await AddSpotifyPlaylistCandidatesAsync(candidates, seen, sourceId, cancellationToken);
        return candidates;
    }

    private async Task AddSpotifyHomeTrendingCandidatesAsync(
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var tracks = await _spotifyPathfinderMetadataClient.FetchBrowseSectionTrackSummariesWithBlobAsync(
            SpotifyTrendingSongsSectionUri,
            0,
            200,
            cancellationToken);

        foreach (var track in tracks)
        {
            AddSpotifyTrackCandidate(
                seen,
                candidates,
                new SpotifyTrackSeed(
                    track.Id,
                    track.Isrc,
                    track.Name,
                    track.Artists,
                    track.Album,
                    track.ReleaseDate,
                    track.Explicit,
                    track.Genres));
        }
    }

    private async Task AddSpotifyArtistTopTrackCandidatesAsync(
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        string artistId,
        CancellationToken cancellationToken)
    {
        var artistPage = await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(
            artistId,
            artistId,
            forceRefresh: true,
            cancellationToken);

        var topTracks = artistPage?.TopTracks;
        if (topTracks == null)
        {
            return;
        }

        for (var i = 0; i < topTracks.Count; i++)
        {
            var track = topTracks[i];
            AddSpotifyTrackCandidate(
                seen,
                candidates,
                new SpotifyTrackSeed(
                    track.Id,
                    track.Isrc,
                    track.Name,
                    artistPage?.Artist?.Name,
                    track.AlbumName,
                    track.ReleaseDate,
                    ExplicitFlag: null,
                    Genres: null));
        }
    }

    private async Task AddSpotifyPlaylistCandidatesAsync(
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        const int hardCap = 1000;
        var offset = 0;

        while (candidates.Count < hardCap)
        {
            var page = await _spotifyMetadataService.FetchPlaylistPageAsync(
                sourceId,
                offset,
                pageSize,
                cancellationToken);

            if (page == null || page.Tracks.Count == 0)
            {
                break;
            }

            foreach (var track in page.Tracks)
            {
                AddSpotifyTrackCandidate(
                    seen,
                    candidates,
                    new SpotifyTrackSeed(
                        track.Id,
                        track.Isrc,
                        track.Name,
                        track.Artists,
                        track.Album,
                        track.ReleaseDate,
                        track.Explicit,
                        track.Genres));
                if (candidates.Count >= hardCap)
                {
                    break;
                }
            }

            if (!page.HasMore)
            {
                break;
            }

            offset += page.Tracks.Count;
            if (page.TotalTracks.HasValue && offset >= page.TotalTracks.Value)
            {
                break;
            }
        }
    }

    private static void AddSpotifyTrackCandidate(
        HashSet<string> seen,
        List<PlaylistTrackCandidate> candidates,
        SpotifyTrackSeed seed)
    {
        var normalizedId = (seed.TrackId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedId) || !seen.Add(normalizedId))
        {
            return;
        }

        int? releaseYear = null;
        if (TryParseReleaseYear(seed.ReleaseDate, out var parsedYear))
        {
            releaseYear = parsedYear;
        }

        candidates.Add(new PlaylistTrackCandidate(
            normalizedId,
            string.IsNullOrWhiteSpace(seed.Isrc) ? null : seed.Isrc.Trim(),
            seed.Title?.Trim() ?? string.Empty,
            seed.Artist?.Trim() ?? string.Empty,
            seed.Album?.Trim() ?? string.Empty,
            releaseYear,
            seed.ExplicitFlag,
            NormalizeGenres(seed.Genres)));
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetDeezerTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_deezerClient.LoggedIn)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var tracks = await _deezerClient.GetPlaylistTracksAsync(sourceId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>(tracks.Count);

        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track.SngId <= 0)
            {
                continue;
            }

            var trackId = track.SngId.ToString();
            if (!seen.Add(trackId))
            {
                continue;
            }

            candidates.Add(new PlaylistTrackCandidate(
                trackId,
                string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                track.SngTitle?.Trim() ?? string.Empty,
                track.ArtName?.Trim() ?? string.Empty,
                track.AlbTitle?.Trim() ?? string.Empty,
                ParseFirstYear(track.PhysicalReleaseDate, track.DigitalReleaseDate),
                track.ExplicitLyrics,
                Array.Empty<string>()));
        }

        return candidates;
    }

    private sealed record SpotifyTrackSeed(
        string? TrackId,
        string? Isrc,
        string? Title,
        string? Artist,
        string? Album,
        string? ReleaseDate,
        bool? ExplicitFlag,
        IReadOnlyList<string>? Genres);

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetSmartTracklistTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!_deezerClient.LoggedIn)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var playlistData = await GetSmartTracklistWatchDataAsync(sourceId, cancellationToken);
        return MapWatchIntentTrackCandidates(playlistData?.Tracks);
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetAppleTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetApplePlaylistWatchDataAsync(sourceId, cancellationToken);
        return MapWatchIntentTrackCandidates(playlistData?.Tracks);
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetBoomplayTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetBoomplayPlaylistWatchDataAsync(sourceId, cancellationToken);
        return MapWatchIntentTrackCandidates(playlistData?.Tracks);
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetRecommendationTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var resolvedLibraryId = 0L;
        if (!TryParseRecommendationLibraryId(sourceId, out resolvedLibraryId))
        {
            var libraries = await _libraryRepository.GetLibrariesAsync(cancellationToken);
            resolvedLibraryId = libraries.Count > 0 ? libraries[0].Id : 0;
        }

        if (resolvedLibraryId <= 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var detail = await _libraryRecommendationService.GetRecommendationsAsync(
            resolvedLibraryId,
            stationId: sourceId,
            limit: 200,
            cancellationToken: cancellationToken);
        if (detail == null || detail.Tracks.Count == 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>(detail.Tracks.Count);
        foreach (var track in detail.Tracks)
        {
            var trackId = (track.Id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trackId) || !seen.Add(trackId))
            {
                continue;
            }

            candidates.Add(new PlaylistTrackCandidate(
                trackId,
                string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                track.Title?.Trim() ?? string.Empty,
                track.Artist?.Name?.Trim() ?? string.Empty,
                track.Album?.Title?.Trim() ?? string.Empty,
                null,
                null,
                Array.Empty<string>()));
        }

        return candidates;
    }

    private static IReadOnlyList<PlaylistTrackCandidate> MapWatchIntentTrackCandidates(
        IReadOnlyCollection<WatchIntentTrack>? tracks)
    {
        if (tracks == null || tracks.Count == 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>(tracks.Count);
        foreach (var track in tracks)
        {
            var trackId = (track.TrackId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trackId) || !seen.Add(trackId))
            {
                continue;
            }

            candidates.Add(new PlaylistTrackCandidate(
                trackId,
                string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                track.Intent.Title?.Trim() ?? string.Empty,
                track.Intent.Artist?.Trim() ?? string.Empty,
                track.Intent.Album?.Trim() ?? string.Empty,
                ParseFirstYear(track.Intent.ReleaseDate),
                track.Intent.Explicit,
                NormalizeGenres(track.Intent.Genres)));
        }

        return candidates;
    }

    private static int? ParseFirstYear(params string?[] values)
    {
        return values
            .Select(TryParseReleaseYearNullable)
            .FirstOrDefault(static year => year.HasValue);
    }

    private static int? TryParseReleaseYearNullable(string? value)
        => TryParseReleaseYear(value, out var year) ? year : null;

    private static IReadOnlyList<string> NormalizeGenres(IReadOnlyCollection<string>? genres)
    {
        if (genres == null || genres.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(genres.Count);
        foreach (var value in genres.Select(static genre => (genre ?? string.Empty).Trim()))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private async Task CheckPlaylistAsync(PlaylistWatchlistDto playlist, CancellationToken cancellationToken)
    {
        var source = NormalizeWatchSource(playlist.Source);
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var preference = await _libraryRepository.GetPlaylistWatchPreferenceAsync(source, playlist.SourceId, cancellationToken);
        var globalBlockRules = await GetGlobalPlaylistBlockRulesAsync(cancellationToken);
        var effectiveBlockRules = PlaylistTrackBlockRuleHelper.MergeRules(preference?.IgnoreRules, globalBlockRules);

        switch (source)
        {
            case SpotifySource:
                await CheckSpotifyPlaylistAsync(playlist, preference, effectiveBlockRules, cancellationToken);
                break;
            case DeezerSource:
                await CheckDeezerPlaylistAsync(playlist, preference, effectiveBlockRules, cancellationToken);
                break;
            case SmartTracklistSource:
                await CheckSmartTracklistAsync(playlist, preference, effectiveBlockRules, cancellationToken);
                break;
            case AppleSource:
                await CheckApplePlaylistAsync(playlist, preference, effectiveBlockRules, cancellationToken);
                break;
            case BoomplaySource:
                await CheckBoomplayPlaylistAsync(playlist, preference, effectiveBlockRules, cancellationToken);
                break;
            case RecommendationsSource:
                await CheckRecommendationsPlaylistAsync(playlist, preference, effectiveBlockRules, cancellationToken);
                break;
            default:
                _logger.LogDebug("Playlist watch skipped for unsupported source: {Source}", source);
                break;
        }
    }

    private async Task<HashSet<string>> GetIgnoredTrackIdsForSourceAsync(
        string source,
        CancellationToken cancellationToken)
    {
        return await _libraryRepository.GetPlaylistWatchIgnoredTrackIdsBySourceAsync(source, cancellationToken);
    }

    private async Task<IReadOnlyList<PlaylistTrackBlockRule>> GetGlobalPlaylistBlockRulesAsync(CancellationToken cancellationToken)
    {
        var preferences = await _libraryRepository.GetPlaylistWatchPreferencesAsync(cancellationToken);
        return PlaylistTrackBlockRuleHelper.BuildGlobalRules(preferences);
    }

    private async Task CheckSpotifyPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        CancellationToken cancellationToken)
    {
        if (IsSpotifyHomeTrendingSourceId(playlist.SourceId))
        {
            await CheckSpotifyHomeTrendingSongsAsync(playlist, preference, effectiveBlockRules, cancellationToken);
            return;
        }

        if (TryGetSpotifyArtistTopTracksSourceId(playlist.SourceId, out var artistId))
        {
            await CheckSpotifyArtistTopTracksAsync(playlist, preference, effectiveBlockRules, artistId, cancellationToken);
            return;
        }

        var settings = _settingsService.LoadSettings();
        var maxItems = Math.Clamp(settings.WatchMaxItemsPerRun, 1, 50);
        var state = await _libraryRepository.GetPlaylistWatchStateAsync(SpotifySource, playlist.SourceId, cancellationToken);
        var (batchOffset, batchSnapshot) = ResolveSpotifyBatchCursor(state);

        var page = await _spotifyMetadataService.FetchPlaylistPageAsync(playlist.SourceId, batchOffset, maxItems, cancellationToken);
        if (page == null)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    SpotifySource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    batchOffset,
                    batchSnapshot,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var snapshotChanged = IsSpotifySnapshotChanged(page, state);
        var metadataChanged = IsSpotifyPlaylistMetadataChanged(page, playlist);
        await UpdateSpotifyPlaylistMetadataIfPresentAsync(playlist, preference, page, cancellationToken);

        if (ShouldSkipSpotifySnapshotProcessing(settings.WatchUseSnapshotIdChecking, page, state, batchSnapshot))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    SpotifySource,
                    playlist.SourceId,
                    page.SnapshotId,
                    page.TotalTracks,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        (page, batchOffset, batchSnapshot) = await RefreshSpotifyBatchPageAsync(
            playlist.SourceId,
            page,
            batchOffset,
            batchSnapshot,
            maxItems,
            cancellationToken);
        var newTracks = await GetNewSpotifyPlaylistTracksAsync(playlist.SourceId, page, cancellationToken);

        if (newTracks.Count > 0)
        {
            var trackInserts = newTracks
                .Select(track => new PlaylistWatchTrackInsert(track.Id, track.Isrc))
                .ToList();
            await _libraryRepository.AddPlaylistWatchTracksAsync(
                SpotifySource,
                playlist.SourceId,
                trackInserts,
                cancellationToken);

            var queuedCount = await QueueSpotifyTracksAsync(
                newTracks,
                preference?.DestinationFolderId,
                BuildQueueWatchOptions(
                    "Spotify",
                    SpotifySource,
                    playlist.SourceId,
                    preference?.PreferredEngine,
                    preference?.DownloadVariantMode,
                    preference?.RoutingRules,
                    effectiveBlockRules),
                cancellationToken);

            if (queuedCount > 0)
            {
                var historyName = page.Name ?? playlist.Name ?? "Playlist";
                await AddPlaylistWatchHistoryAsync(
                    SpotifySource,
                    playlist.SourceId,
                    historyName,
                    newTracks.Count,
                    QueuedStatus,
                    cancellationToken);
            }
        }

        var (nextBatchOffset, nextBatchSnapshot) = ResolveNextSpotifyBatchCursor(page, batchOffset, batchSnapshot);
        var totalTracks = page.TotalTracks;

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                SpotifySource,
                playlist.SourceId,
                page.SnapshotId,
                totalTracks,
                nextBatchOffset,
                nextBatchSnapshot,
                DateTimeOffset.UtcNow),
            cancellationToken);

        if ((snapshotChanged || metadataChanged) && !string.IsNullOrWhiteSpace(preference?.Service))
        {
            var result = await _playlistSyncService.SyncSpotifyPlaylistAsync(
                playlist,
                preference,
                force: false,
                cancellationToken);
            if (!result.Success)
            {
                _logger.LogDebug("Spotify playlist sync skipped: {Message}", result.Message);
            }
        }
    }

    private static (int BatchOffset, string? BatchSnapshot) ResolveSpotifyBatchCursor(PlaylistWatchStateDto? state)
    {
        var batchOffset = state?.BatchNextOffset ?? 0;
        if (batchOffset < 0)
        {
            batchOffset = 0;
        }

        return (batchOffset, state?.BatchProcessingSnapshotId);
    }

    private static bool IsSpotifySnapshotChanged(SpotifyPlaylistPage page, PlaylistWatchStateDto? state)
    {
        return !string.IsNullOrWhiteSpace(page.SnapshotId)
               && !string.Equals(page.SnapshotId, state?.SnapshotId, StringComparison.Ordinal);
    }

    private static bool IsSpotifyPlaylistMetadataChanged(SpotifyPlaylistPage page, PlaylistWatchlistDto playlist)
    {
        var nameChanged = !string.IsNullOrWhiteSpace(page.Name)
                          && !string.Equals(page.Name, playlist.Name, StringComparison.Ordinal);
        var descriptionChanged = !string.IsNullOrWhiteSpace(page.Description)
                                 && !string.Equals(page.Description, playlist.Description, StringComparison.Ordinal);
        var imageChanged = !string.IsNullOrWhiteSpace(page.ImageUrl)
                           && !string.Equals(page.ImageUrl, playlist.ImageUrl, StringComparison.Ordinal);
        var trackCountChanged = page.TotalTracks.HasValue && page.TotalTracks != playlist.TrackCount;
        return nameChanged || descriptionChanged || imageChanged || trackCountChanged;
    }

    private async Task UpdateSpotifyPlaylistMetadataIfPresentAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        SpotifyPlaylistPage page,
        CancellationToken cancellationToken)
    {
        if (!HasSpotifyPlaylistMetadataPayload(page))
        {
            return;
        }

        var imageUrl = await ResolvePlaylistImageUrlAsync(
            SpotifySource,
            playlist.SourceId,
            page.Name ?? playlist.Name,
            page.ImageUrl,
            preference,
            cancellationToken);
        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            SpotifySource,
            playlist.SourceId,
            page.Name,
            imageUrl,
            page.Description,
            page.TotalTracks,
            cancellationToken);
    }

    private static bool HasSpotifyPlaylistMetadataPayload(SpotifyPlaylistPage page)
    {
        return !string.IsNullOrWhiteSpace(page.Name)
               || !string.IsNullOrWhiteSpace(page.Description)
               || !string.IsNullOrWhiteSpace(page.ImageUrl)
               || page.TotalTracks.HasValue;
    }

    private static bool ShouldSkipSpotifySnapshotProcessing(
        bool useSnapshotIdChecking,
        SpotifyPlaylistPage page,
        PlaylistWatchStateDto? state,
        string? batchSnapshot)
    {
        return useSnapshotIdChecking
               && !string.IsNullOrWhiteSpace(page.SnapshotId)
               && string.Equals(page.SnapshotId, state?.SnapshotId, StringComparison.Ordinal)
               && string.IsNullOrWhiteSpace(batchSnapshot);
    }

    private async Task<(SpotifyPlaylistPage Page, int BatchOffset, string? BatchSnapshot)> RefreshSpotifyBatchPageAsync(
        string sourceId,
        SpotifyPlaylistPage page,
        int batchOffset,
        string? batchSnapshot,
        int maxItems,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(page.SnapshotId)
            || string.Equals(page.SnapshotId, batchSnapshot, StringComparison.Ordinal))
        {
            return (page, batchOffset, batchSnapshot);
        }

        batchSnapshot = page.SnapshotId;
        batchOffset = 0;
        page = await _spotifyMetadataService.FetchPlaylistPageAsync(sourceId, batchOffset, maxItems, cancellationToken)
            ?? page;
        return (page, batchOffset, batchSnapshot);
    }

    private async Task<List<SpotifyTrackSummary>> GetNewSpotifyPlaylistTracksAsync(
        string sourceId,
        SpotifyPlaylistPage page,
        CancellationToken cancellationToken)
    {
        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(SpotifySource, sourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(SpotifySource, cancellationToken);
        return page.Tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id)
                            && !existing.Contains(track.Id)
                            && !ignored.Contains(track.Id))
            .ToList();
    }

    private static (int NextBatchOffset, string? NextBatchSnapshot) ResolveNextSpotifyBatchCursor(
        SpotifyPlaylistPage page,
        int batchOffset,
        string? batchSnapshot)
    {
        var nextOffset = batchOffset + page.Tracks.Count;
        var hasMore = page.HasMore
                      && (!page.TotalTracks.HasValue || nextOffset < page.TotalTracks.Value);
        return hasMore ? (nextOffset, batchSnapshot) : (0, null);
    }

    private async Task CheckSpotifyArtistTopTracksAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        string artistId,
        CancellationToken cancellationToken)
    {
        _ = preference;
        _ = effectiveBlockRules;
        var artistPage = await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(
            artistId,
            artistId,
            forceRefresh: true,
            cancellationToken);

        var state = await _libraryRepository.GetPlaylistWatchStateAsync(SpotifySource, playlist.SourceId, cancellationToken);
        if (artistPage?.Artist == null)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    SpotifySource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var artistName = string.IsNullOrWhiteSpace(artistPage.Artist.Name)
            ? (playlist.Name ?? "Top Songs")
            : artistPage.Artist.Name.Trim();
        var listName = $"{artistName} - Top Songs";
        var imageUrl = artistPage.Artist.Images?
            .OrderByDescending(image => image.Width ?? 0)
            .ThenByDescending(image => image.Height ?? 0)
            .Select(image => image.Url)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var topTracks = (artistPage.TopTracks ?? new List<SpotifyTrack>())
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .Select(track => new
            {
                Id = track.Id.Trim(),
                Isrc = string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim()
            })
            .Where(track => seen.Add(track.Id))
            .ToList();

        var snapshotId = BuildSpotifyTopTracksSnapshot(topTracks.Select(track => track.Id));
        var trackTotal = topTracks.Count;
        imageUrl = await ResolvePlaylistImageUrlAsync(
            SpotifySource,
            playlist.SourceId,
            listName,
            imageUrl,
            preference,
            cancellationToken);

        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            SpotifySource,
            playlist.SourceId,
            listName,
            imageUrl,
            "Spotify artist top tracks",
            trackTotal,
            cancellationToken);

        var settings = _settingsService.LoadSettings();
        if (settings.WatchUseSnapshotIdChecking
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(snapshotId, state?.SnapshotId, StringComparison.Ordinal))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    SpotifySource,
                    playlist.SourceId,
                    snapshotId,
                    trackTotal,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(SpotifySource, playlist.SourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(SpotifySource, cancellationToken);
        var newTracks = topTracks
            .Where(track => !existing.Contains(track.Id) && !ignored.Contains(track.Id))
            .ToList();

        if (newTracks.Count > 0)
        {
            var trackInserts = newTracks
                .Select(track => new PlaylistWatchTrackInsert(track.Id, track.Isrc))
                .ToList();
            await _libraryRepository.AddPlaylistWatchTracksAsync(
                SpotifySource,
                playlist.SourceId,
                trackInserts,
                cancellationToken);

            await AddPlaylistWatchHistoryAsync(
                SpotifySource,
                playlist.SourceId,
                listName,
                newTracks.Count,
                "detected",
                cancellationToken,
                artistName);
        }

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                SpotifySource,
                playlist.SourceId,
                snapshotId,
                trackTotal,
                null,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task CheckSpotifyHomeTrendingSongsAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var maxItems = Math.Clamp(settings.WatchMaxItemsPerRun, 1, 50);
        var state = await _libraryRepository.GetPlaylistWatchStateAsync(SpotifySource, playlist.SourceId, cancellationToken);

        var fetchedTracks = await _spotifyPathfinderMetadataClient.FetchBrowseSectionTrackSummariesWithBlobAsync(
            SpotifyTrendingSongsSectionUri,
            0,
            maxItems,
            cancellationToken);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracks = fetchedTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .Select(track => new
            {
                Track = track,
                Id = track.Id.Trim(),
                Isrc = string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim()
            })
            .Where(item => seen.Add(item.Id))
            .ToList();

        if (tracks.Count == 0)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    SpotifySource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var listName = string.IsNullOrWhiteSpace(playlist.Name)
            ? "Trending songs"
            : playlist.Name.Trim();
        var imageUrl = tracks
            .Select(item => item.Track.ImageUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
            ?? playlist.ImageUrl;
        var description = "Spotify home feed trending songs";
        var snapshotId = BuildSpotifyTopTracksSnapshot(tracks.Select(item => item.Id));
        var trackTotal = tracks.Count;
        imageUrl = await ResolvePlaylistImageUrlAsync(
            SpotifySource,
            playlist.SourceId,
            listName,
            imageUrl,
            preference,
            cancellationToken);

        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            SpotifySource,
            playlist.SourceId,
            listName,
            imageUrl,
            description,
            trackTotal,
            cancellationToken);

        if (settings.WatchUseSnapshotIdChecking
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(snapshotId, state?.SnapshotId, StringComparison.Ordinal))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    SpotifySource,
                    playlist.SourceId,
                    snapshotId,
                    trackTotal,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(SpotifySource, playlist.SourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(SpotifySource, cancellationToken);
        var newTracks = tracks
            .Where(track => !existing.Contains(track.Id) && !ignored.Contains(track.Id))
            .ToList();

        if (newTracks.Count > 0)
        {
            var trackInserts = newTracks
                .Select(track => new PlaylistWatchTrackInsert(track.Id, track.Isrc))
                .ToList();
            await _libraryRepository.AddPlaylistWatchTracksAsync(
                SpotifySource,
                playlist.SourceId,
                trackInserts,
                cancellationToken);

            var queuedCount = await QueueSpotifyTracksAsync(
                newTracks.Select(track => track.Track).ToList(),
                preference?.DestinationFolderId,
                BuildQueueWatchOptions(
                    "Spotify",
                    SpotifySource,
                    playlist.SourceId,
                    preference?.PreferredEngine,
                    preference?.DownloadVariantMode,
                    preference?.RoutingRules,
                    effectiveBlockRules),
                cancellationToken);

            await AddPlaylistWatchHistoryAsync(
                SpotifySource,
                playlist.SourceId,
                listName,
                newTracks.Count,
                queuedCount > 0 ? QueuedStatus : "detected",
                cancellationToken);
        }

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                SpotifySource,
                playlist.SourceId,
                snapshotId,
                trackTotal,
                null,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task CheckDeezerPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        CancellationToken cancellationToken)
    {
        if (!_deezerClient.LoggedIn)
        {
            _logger.LogDebug("Deezer playlist watch skipped - not logged in.");
            return;
        }

        ApiPlaylist playlistInfo;
        try
        {
            playlistInfo = await _deezerClient.GetPlaylistAsync(playlist.SourceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer playlist watch failed for {SourceId}; attempting smarttracklist mode.", playlist.SourceId);
            await CheckSmartTracklistAsync(playlist, preference, effectiveBlockRules, DeezerSource, cancellationToken);
            return;
        }

        var snapshotId = string.IsNullOrWhiteSpace(playlistInfo.Checksum) ? null : playlistInfo.Checksum;
        var trackTotal = playlistInfo.NbTracks;
        if (!string.IsNullOrWhiteSpace(playlistInfo.Title) || !string.IsNullOrWhiteSpace(playlistInfo.Description) || !string.IsNullOrWhiteSpace(playlistInfo.PictureXl) || trackTotal.HasValue)
        {
            var imageUrl = !string.IsNullOrWhiteSpace(playlistInfo.PictureXl)
                ? playlistInfo.PictureXl
                : playlistInfo.PictureBig;
            imageUrl = await ResolvePlaylistImageUrlAsync(
                DeezerSource,
                playlist.SourceId,
                playlistInfo.Title ?? playlist.Name,
                imageUrl,
                preference,
                cancellationToken);
            await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
                DeezerSource,
                playlist.SourceId,
                playlistInfo.Title,
                imageUrl,
                playlistInfo.Description,
                trackTotal,
                cancellationToken);
        }
        var state = await _libraryRepository.GetPlaylistWatchStateAsync(DeezerSource, playlist.SourceId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(snapshotId, state?.SnapshotId, StringComparison.Ordinal))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    DeezerSource,
                    playlist.SourceId,
                    snapshotId,
                    trackTotal,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var tracks = await _deezerClient.GetPlaylistTracksAsync(playlist.SourceId);
        if (tracks.Count == 0)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    DeezerSource,
                    playlist.SourceId,
                    snapshotId,
                    trackTotal,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(DeezerSource, playlist.SourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(DeezerSource, cancellationToken);
        var newTracks = tracks
            .Where(track => track.SngId > 0
                            && !existing.Contains(track.SngId.ToString())
                            && !ignored.Contains(track.SngId.ToString()))
            .ToList();

        if (newTracks.Count == 0)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    DeezerSource,
                    playlist.SourceId,
                    snapshotId,
                    trackTotal,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var trackInserts = newTracks
            .Select(track => new PlaylistWatchTrackInsert(track.SngId.ToString(), track.Isrc))
            .ToList();
        await _libraryRepository.AddPlaylistWatchTracksAsync(
            DeezerSource,
            playlist.SourceId,
            trackInserts,
            cancellationToken);

        var playlistName = playlistInfo.Title ?? playlist.Name ?? "Playlist";
        var queuedCount = await QueueDeezerTracksAsync(
            newTracks,
            preference?.DestinationFolderId,
            BuildQueueWatchOptions(
                "Deezer",
                DeezerSource,
                playlist.SourceId,
                preference?.PreferredEngine,
                preference?.DownloadVariantMode,
                preference?.RoutingRules,
                effectiveBlockRules),
            cancellationToken);

        if (queuedCount > 0)
        {
            await AddPlaylistWatchHistoryAsync(
                DeezerSource,
                playlist.SourceId,
                playlistName,
                newTracks.Count,
                QueuedStatus,
                cancellationToken);
        }

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                DeezerSource,
                playlist.SourceId,
                snapshotId,
                trackTotal,
                null,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task CheckApplePlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        CancellationToken cancellationToken)
    {
        var state = await _libraryRepository.GetPlaylistWatchStateAsync(AppleSource, playlist.SourceId, cancellationToken);
        ApplePlaylistWatchData? playlistData;
        try
        {
            playlistData = await GetApplePlaylistWatchDataAsync(playlist.SourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple playlist watch failed to load playlist {SourceId}.", playlist.SourceId);
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    AppleSource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        if (playlistData == null)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    AppleSource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var appleImageUrl = await ResolvePlaylistImageUrlAsync(
            AppleSource,
            playlist.SourceId,
            playlistData.Name,
            playlistData.ImageUrl,
            preference,
            cancellationToken);
        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            AppleSource,
            playlist.SourceId,
            playlistData.Name,
            appleImageUrl,
            playlistData.Description,
            playlistData.TrackCount,
            cancellationToken);

        var snapshotId = BuildTrackIdSnapshot(playlistData.Tracks.Select(track => track.TrackId));
        var settings = _settingsService.LoadSettings();
        if (settings.WatchUseSnapshotIdChecking
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(snapshotId, state?.SnapshotId, StringComparison.Ordinal))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    AppleSource,
                    playlist.SourceId,
                    snapshotId,
                    playlistData.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(AppleSource, playlist.SourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(AppleSource, cancellationToken);
        var newTracks = playlistData.Tracks
            .Where(track => !existing.Contains(track.TrackId)
                            && !ignored.Contains(track.TrackId))
            .ToList();

        if (newTracks.Count > 0)
        {
            var trackInserts = newTracks
                .Select(track => new PlaylistWatchTrackInsert(track.TrackId, track.Isrc))
                .ToList();
            await _libraryRepository.AddPlaylistWatchTracksAsync(
                AppleSource,
                playlist.SourceId,
                trackInserts,
                cancellationToken);

            var queuedCount = await QueueAppleTracksAsync(
                newTracks,
                preference?.DestinationFolderId,
                BuildQueueWatchOptions(
                    "Apple Music",
                    AppleSource,
                    playlist.SourceId,
                    preference?.PreferredEngine,
                    preference?.DownloadVariantMode,
                    preference?.RoutingRules,
                    effectiveBlockRules),
                cancellationToken);

            if (queuedCount > 0)
            {
                await AddPlaylistWatchHistoryAsync(
                    AppleSource,
                    playlist.SourceId,
                    playlistData.Name,
                    newTracks.Count,
                    QueuedStatus,
                    cancellationToken);
            }
        }

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                AppleSource,
                playlist.SourceId,
                snapshotId,
                playlistData.TrackCount,
                null,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task CheckBoomplayPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        CancellationToken cancellationToken)
    {
        var state = await _libraryRepository.GetPlaylistWatchStateAsync(BoomplaySource, playlist.SourceId, cancellationToken);
        BoomplayPlaylistWatchData? playlistData;
        try
        {
            playlistData = await GetBoomplayPlaylistWatchDataAsync(playlist.SourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Boomplay playlist watch failed to load playlist {SourceId}.", playlist.SourceId);
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    BoomplaySource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        if (playlistData == null)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    BoomplaySource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var boomplayImageUrl = await ResolvePlaylistImageUrlAsync(
            BoomplaySource,
            playlist.SourceId,
            playlistData.Name,
            playlistData.ImageUrl,
            preference,
            cancellationToken);
        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            BoomplaySource,
            playlist.SourceId,
            playlistData.Name,
            boomplayImageUrl,
            playlistData.Description,
            playlistData.TrackCount,
            cancellationToken);

        var snapshotId = BuildTrackIdSnapshot(playlistData.Tracks.Select(track => track.TrackId));
        var settings = _settingsService.LoadSettings();
        if (settings.WatchUseSnapshotIdChecking
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(snapshotId, state?.SnapshotId, StringComparison.Ordinal))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    BoomplaySource,
                    playlist.SourceId,
                    snapshotId,
                    playlistData.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(BoomplaySource, playlist.SourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(BoomplaySource, cancellationToken);
        var newTracks = playlistData.Tracks
            .Where(track => !existing.Contains(track.TrackId)
                            && !ignored.Contains(track.TrackId))
            .ToList();

        if (newTracks.Count > 0)
        {
            var trackInserts = newTracks
                .Select(track => new PlaylistWatchTrackInsert(track.TrackId, track.Isrc))
                .ToList();
            await _libraryRepository.AddPlaylistWatchTracksAsync(
                BoomplaySource,
                playlist.SourceId,
                trackInserts,
                cancellationToken);

            var queuedCount = await QueueBoomplayTracksAsync(
                newTracks,
                preference?.DestinationFolderId,
                BuildQueueWatchOptions(
                    "Boomplay",
                    BoomplaySource,
                    playlist.SourceId,
                    preference?.PreferredEngine,
                    preference?.DownloadVariantMode,
                    preference?.RoutingRules,
                    effectiveBlockRules),
                cancellationToken);

            await AddPlaylistWatchHistoryAsync(
                BoomplaySource,
                playlist.SourceId,
                playlistData.Name,
                queuedCount,
                QueuedStatus,
                cancellationToken);
        }

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                BoomplaySource,
                playlist.SourceId,
                snapshotId,
                playlistData.TrackCount,
                null,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private Task CheckSmartTracklistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        CancellationToken cancellationToken)
    {
        return CheckSmartTracklistAsync(playlist, preference, effectiveBlockRules, SmartTracklistSource, cancellationToken);
    }

    private async Task CheckSmartTracklistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        string persistedSource,
        CancellationToken cancellationToken)
    {
        if (!_deezerClient.LoggedIn)
        {
            _logger.LogDebug("Smarttracklist watch skipped - not logged in to Deezer.");
            return;
        }

        var state = await _libraryRepository.GetPlaylistWatchStateAsync(persistedSource, playlist.SourceId, cancellationToken);
        SmartTracklistWatchData? playlistData;
        try
        {
            playlistData = await GetSmartTracklistWatchDataAsync(playlist.SourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Smarttracklist watch failed to load playlist {SourceId}.", playlist.SourceId);
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    persistedSource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        if (playlistData == null)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    persistedSource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var smartTracklistImageUrl = await ResolvePlaylistImageUrlAsync(
            persistedSource,
            playlist.SourceId,
            playlistData.Name,
            playlistData.ImageUrl,
            preference,
            cancellationToken);
        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            persistedSource,
            playlist.SourceId,
            playlistData.Name,
            smartTracklistImageUrl,
            playlistData.Description,
            playlistData.TrackCount,
            cancellationToken);

        var snapshotId = BuildTrackIdSnapshot(playlistData.Tracks.Select(track => track.TrackId));
        var settings = _settingsService.LoadSettings();
        if (settings.WatchUseSnapshotIdChecking
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(snapshotId, state?.SnapshotId, StringComparison.Ordinal))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    persistedSource,
                    playlist.SourceId,
                    snapshotId,
                    playlistData.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(persistedSource, playlist.SourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(persistedSource, cancellationToken);
        var newTracks = playlistData.Tracks
            .Where(track => !existing.Contains(track.TrackId)
                            && !ignored.Contains(track.TrackId))
            .ToList();

        if (newTracks.Count > 0)
        {
            var trackInserts = newTracks
                .Select(track => new PlaylistWatchTrackInsert(track.TrackId, track.Isrc))
                .ToList();
            await _libraryRepository.AddPlaylistWatchTracksAsync(
                persistedSource,
                playlist.SourceId,
                trackInserts,
                cancellationToken);

            var queuedCount = await QueueSmartTracklistTracksAsync(
                newTracks,
                preference?.DestinationFolderId,
                BuildQueueWatchOptions(
                    "Deezer",
                    persistedSource,
                    playlist.SourceId,
                    preference?.PreferredEngine,
                    preference?.DownloadVariantMode,
                    preference?.RoutingRules,
                    effectiveBlockRules),
                cancellationToken);

            await AddPlaylistWatchHistoryAsync(
                persistedSource,
                playlist.SourceId,
                playlistData.Name,
                queuedCount,
                QueuedStatus,
                cancellationToken);
        }

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                persistedSource,
                playlist.SourceId,
                snapshotId,
                playlistData.TrackCount,
                null,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task<SmartTracklistWatchData?> GetSmartTracklistWatchDataAsync(
        string smartTracklistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(smartTracklistId))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var page = await _deezerGatewayService.GetSmartTracklistPageAsync(smartTracklistId);
        var results = page["results"] as JObject ?? page;
        var data = results["DATA"] as JObject ?? results["data"] as JObject;
        var songs = results["SONGS"] as JObject ?? results["songs"] as JObject;
        var songsData = songs?["data"] as JArray ?? songs?["DATA"] as JArray;
        if (data == null || songsData == null)
        {
            return null;
        }

        var title = data.Value<string>("TITLE")?.Trim();
        var description = data.Value<string>("DESCRIPTION");
        var cover = data["COVER"] as JObject;
        var coverMd5 = cover?.Value<string>("MD5")
            ?? cover?.Value<string>("md5")
            ?? data.Value<string>("COVER");
        var imageUrl = BuildDeezerCoverUrl(coverMd5);
        var tracks = new List<WatchIntentTrack>();

        foreach (var token in songsData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token is not JObject track)
            {
                continue;
            }

            var trackId = (track.Value<string>("SNG_ID")
                          ?? track.Value<string>("id")
                          ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            var isrc = track.Value<string>("ISRC")
                ?? track.Value<string>("isrc")
                ?? string.Empty;
            var artistName = track.Value<string>("ART_NAME")
                ?? track["artist"]?.Value<string>("name")
                ?? string.Empty;
            var albumTitle = track.Value<string>("ALB_TITLE")
                ?? track[AlbumField]?.Value<string>("title")
                ?? string.Empty;
            var albumCoverId = track.Value<string>("ALB_PICTURE")
                ?? track[AlbumField]?.Value<string>("md5_image")
                ?? track[AlbumField]?.Value<string>("cover");
            var durationSeconds = track.Value<int?>("DURATION")
                ?? track.Value<int?>("duration")
                ?? 0;
            var position = track.Value<int?>("TRACK_NUMBER")
                ?? track.Value<int?>("POSITION")
                ?? tracks.Count + 1;
            var coverUrl = BuildDeezerCoverUrl(albumCoverId);
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                coverUrl = imageUrl;
            }
            var intent = new DownloadIntent
            {
                SourceService = DeezerSource,
                SourceUrl = BuildDeezerTrackUrl(trackId),
                DeezerId = trackId,
                Isrc = isrc,
                Title = track.Value<string>("SNG_TITLE")
                    ?? track.Value<string>("title")
                    ?? string.Empty,
                Artist = artistName,
                Album = albumTitle,
                AlbumArtist = artistName,
                Cover = coverUrl,
                DurationMs = durationSeconds > 0 ? durationSeconds * 1000 : 0,
                Position = position,
                TrackNumber = position
            };

            tracks.Add(new WatchIntentTrack(trackId, isrc, intent));
        }

        var trackCount = data.Value<int?>("NB_SONG")
            ?? songsData.Count;
        return new SmartTracklistWatchData(
            string.IsNullOrWhiteSpace(title) ? "Smart Tracklist" : title,
            description,
            imageUrl,
            trackCount,
            tracks);
    }

    private async Task CheckRecommendationsPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackBlockRule>? effectiveBlockRules,
        CancellationToken cancellationToken)
    {
        var persistedSource = RecommendationsSource;
        var state = await _libraryRepository.GetPlaylistWatchStateAsync(persistedSource, playlist.SourceId, cancellationToken);

        var resolvedLibraryId = 0L;
        if (!TryParseRecommendationLibraryId(playlist.SourceId, out resolvedLibraryId))
        {
            var libraries = await _libraryRepository.GetLibrariesAsync(cancellationToken);
            resolvedLibraryId = libraries.Count > 0 ? libraries[0].Id : 0;
        }

        if (resolvedLibraryId <= 0)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    persistedSource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        RecommendationDetailDto? detail;
        try
        {
            detail = await _libraryRecommendationService.GetRecommendationsAsync(
                resolvedLibraryId,
                stationId: playlist.SourceId,
                limit: 50,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Recommendations watch failed to load station {SourceId}.", playlist.SourceId);
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    persistedSource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        if (detail == null)
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    persistedSource,
                    playlist.SourceId,
                    state?.SnapshotId,
                    state?.TrackCount,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var playlistName = string.IsNullOrWhiteSpace(detail.Station.Name) ? (playlist.Name ?? "Recommendations") : detail.Station.Name;
        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            persistedSource,
            playlist.SourceId,
            playlistName,
            detail.Station.ImageUrl ?? playlist.ImageUrl,
            detail.Station.Description,
            detail.Tracks.Count,
            cancellationToken);

        var snapshotId = BuildTrackIdSnapshot(detail.Tracks.Select(track => track.Id));
        var settings = _settingsService.LoadSettings();
        if (settings.WatchUseSnapshotIdChecking
            && !string.IsNullOrWhiteSpace(snapshotId)
            && string.Equals(snapshotId, state?.SnapshotId, StringComparison.Ordinal))
        {
            await UpsertPlaylistWatchStateAsync(
                new LibraryRepository.PlaylistWatchStateUpsertInput(
                    persistedSource,
                    playlist.SourceId,
                    snapshotId,
                    detail.Tracks.Count,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var existing = await _libraryRepository.GetPlaylistWatchTrackIdsAsync(persistedSource, playlist.SourceId, cancellationToken);
        var ignored = await GetIgnoredTrackIdsForSourceAsync(persistedSource, cancellationToken);
        var newTracks = detail.Tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id)
                            && !existing.Contains(track.Id)
                            && !ignored.Contains(track.Id))
            .ToList();

        if (newTracks.Count > 0)
        {
            var trackInserts = newTracks
                .Select(track => new PlaylistWatchTrackInsert(track.Id, track.Isrc))
                .ToList();
            await _libraryRepository.AddPlaylistWatchTracksAsync(
                persistedSource,
                playlist.SourceId,
                trackInserts,
                cancellationToken);

            var queuedCount = await QueueRecommendationTracksAsync(
                newTracks,
                preference?.DestinationFolderId,
                BuildQueueWatchOptions(
                    "Recommendations",
                    persistedSource,
                    playlist.SourceId,
                    preference?.PreferredEngine,
                    preference?.DownloadVariantMode,
                    preference?.RoutingRules,
                    effectiveBlockRules),
                cancellationToken);

            await AddPlaylistWatchHistoryAsync(
                persistedSource,
                playlist.SourceId,
                playlistName,
                queuedCount,
                QueuedStatus,
                cancellationToken);
        }

        await UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                persistedSource,
                playlist.SourceId,
                snapshotId,
                detail.Tracks.Count,
                null,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task<ApplePlaylistWatchData?> GetApplePlaylistWatchDataAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        var settings = _settingsService.LoadSettings();
        var storefront = await _appleCatalogService.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);

        using var doc = await _appleCatalogService.GetPlaylistAsync(
            playlistId,
            storefront,
            language: "en-US",
            cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var dataArr)
            || dataArr.ValueKind != JsonValueKind.Array
            || dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var playlist = dataArr[0];
        if (!playlist.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetJsonString(attributes, "name") ?? "Apple Playlist";
        var description = GetJsonString(attributes, "description");
        var imageUrl = ResolveAppleArtworkUrl(attributes);
        int? trackCount = GetJsonInt(attributes, "trackCount");
        var tracks = new List<WatchIntentTrack>();

        if (TryGetApplePlaylistTracksData(playlist, out var tracksData))
        {
            foreach (var track in tracksData.EnumerateArray())
            {
                var watchTrack = BuildApplePlaylistWatchTrack(track, storefront, imageUrl);
                if (watchTrack is not null)
                {
                    tracks.Add(watchTrack);
                }
            }
        }

        if (!trackCount.HasValue)
        {
            trackCount = tracks.Count;
        }

        return new ApplePlaylistWatchData(name, description, imageUrl, trackCount, tracks);
    }

    private static bool TryGetApplePlaylistTracksData(JsonElement playlist, out JsonElement tracksData)
    {
        tracksData = default;
        return playlist.TryGetProperty("relationships", out var relationships)
               && relationships.ValueKind == JsonValueKind.Object
               && relationships.TryGetProperty("tracks", out var tracksRel)
               && tracksRel.ValueKind == JsonValueKind.Object
               && tracksRel.TryGetProperty("data", out tracksData)
               && tracksData.ValueKind == JsonValueKind.Array;
    }

    private static WatchIntentTrack? BuildApplePlaylistWatchTrack(
        JsonElement track,
        string storefront,
        string? fallbackImageUrl)
    {
        if (track.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var trackId = GetJsonString(track, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        if (!track.TryGetProperty("attributes", out var trackAttributes)
            || trackAttributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sourceUrl = GetJsonString(trackAttributes, "url");
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceUrl = $"https://music.apple.com/{storefront}/song/{trackId}?i={trackId}";
        }

        var intent = new DownloadIntent
        {
            SourceService = AppleSource,
            SourceUrl = sourceUrl ?? string.Empty,
            AppleId = trackId,
            Isrc = GetJsonString(trackAttributes, "isrc") ?? string.Empty,
            Title = GetJsonString(trackAttributes, "name") ?? string.Empty,
            Artist = GetJsonString(trackAttributes, "artistName") ?? string.Empty,
            Album = GetJsonString(trackAttributes, "albumName") ?? string.Empty,
            AlbumArtist = GetJsonString(trackAttributes, "artistName") ?? string.Empty,
            Cover = ResolveAppleArtworkUrl(trackAttributes) ?? fallbackImageUrl ?? string.Empty,
            DurationMs = GetJsonInt(trackAttributes, "durationInMillis") ?? 0,
            TrackNumber = GetJsonInt(trackAttributes, "trackNumber") ?? 0,
            DiscNumber = GetJsonInt(trackAttributes, "discNumber") ?? 0,
            ReleaseDate = GetJsonString(trackAttributes, "releaseDate") ?? string.Empty,
            Explicit = string.Equals(GetJsonString(trackAttributes, "contentRating"), "explicit", StringComparison.OrdinalIgnoreCase)
                ? true
                : null,
            Composer = GetJsonString(trackAttributes, "composerName") ?? string.Empty,
            Genres = ReadJsonStringArray(trackAttributes, "genreNames")
        };

        return new WatchIntentTrack(trackId, intent.Isrc, intent);
    }

    private async Task<BoomplayPlaylistWatchData?> GetBoomplayPlaylistWatchDataAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        BoomplayPlaylistMetadata? playlist = string.Equals(playlistId, "trending-songs", StringComparison.OrdinalIgnoreCase)
            ? await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: true, cancellationToken)
            : await _boomplayMetadataService.GetPlaylistAsync(playlistId, includeTracks: true, cancellationToken);

        if (playlist == null)
        {
            return null;
        }

        var tracks = playlist.Tracks.Count > 0
            ? BuildBoomplayWatchTracksFromPlaylistItems(playlist.Tracks)
            : BuildBoomplayWatchTracksFromHints(playlist.TrackIds, playlist.TrackHints);

        var trackCount = playlist.TrackIds.Count > 0
            ? playlist.TrackIds.Count
            : tracks.Count;

        return new BoomplayPlaylistWatchData(
            string.IsNullOrWhiteSpace(playlist.Title) ? "Boomplay Playlist" : playlist.Title,
            playlist.Description,
            playlist.ImageUrl,
            trackCount,
            tracks);
    }

    private static List<WatchIntentTrack> BuildBoomplayWatchTracksFromPlaylistItems(
        IReadOnlyCollection<BoomplayTrackMetadata> tracks)
    {
        var watchTracks = new List<WatchIntentTrack>(tracks.Count);
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Id))
            {
                continue;
            }

            var trackId = track.Id.Trim();
            var sourceUrl = string.IsNullOrWhiteSpace(track.Url)
                ? $"https://www.boomplay.com/songs/{trackId}"
                : track.Url;

            var intent = new DownloadIntent
            {
                SourceService = BoomplaySource,
                SourceUrl = sourceUrl,
                Isrc = track.Isrc ?? string.Empty,
                Title = track.Title ?? string.Empty,
                Artist = track.Artist ?? string.Empty,
                Album = track.Album ?? string.Empty,
                AlbumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist)
                    ? track.Artist ?? string.Empty
                    : track.AlbumArtist,
                Cover = track.CoverUrl ?? string.Empty,
                DurationMs = track.DurationMs,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber,
                ReleaseDate = track.ReleaseDate ?? string.Empty,
                Composer = track.Composer ?? string.Empty,
                Genres = track.Genres?
                    .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                    .Select(static genre => genre.Trim())
                    .ToList() ?? new List<string>()
            };

            watchTracks.Add(new WatchIntentTrack(trackId, intent.Isrc, intent));
        }

        return watchTracks;
    }

    private static List<WatchIntentTrack> BuildBoomplayWatchTracksFromHints(
        IReadOnlyCollection<string> trackIds,
        Dictionary<string, BoomplayTrackHint> trackHints)
    {
        var watchTracks = new List<WatchIntentTrack>(trackIds.Count);
        foreach (var trackId in trackIds.Select(static trackIdRaw => trackIdRaw?.Trim()))
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            trackHints.TryGetValue(trackId, out var hint);
            var intent = new DownloadIntent
            {
                SourceService = BoomplaySource,
                SourceUrl = $"https://www.boomplay.com/songs/{trackId}",
                Title = hint?.Title ?? string.Empty,
                Artist = hint?.Artist ?? string.Empty,
                Album = hint?.Album ?? string.Empty,
                AlbumArtist = hint?.Artist ?? string.Empty,
                Cover = hint?.CoverUrl ?? string.Empty
            };

            watchTracks.Add(new WatchIntentTrack(trackId, null, intent));
        }

        return watchTracks;
    }

    public Task<int> QueueSpotifyWatchTracksAsync(
        string collectionName,
        string collectionType,
        IReadOnlyCollection<SpotifyTrackSummary> tracks,
        long? destinationFolderId,
        CancellationToken cancellationToken)
    {
        var sourceLabel = BuildQueueSourceLabel("Spotify", collectionType, collectionName);
        return QueueSpotifyTracksAsync(
            tracks,
            destinationFolderId,
            BuildQueueWatchOptions(sourceLabel, null, null),
            cancellationToken);
    }

    public Task<int> QueueDeezerWatchTracksAsync(
        string collectionName,
        string collectionType,
        IReadOnlyCollection<GwTrack> tracks,
        long? destinationFolderId,
        CancellationToken cancellationToken)
    {
        var sourceLabel = BuildQueueSourceLabel("Deezer", collectionType, collectionName);
        return QueueDeezerTracksAsync(
            tracks,
            destinationFolderId,
            BuildQueueWatchOptions(sourceLabel, null, null),
            cancellationToken);
    }

    public Task<int> QueueAppleWatchIntentsAsync(
        string collectionName,
        string collectionType,
        IReadOnlyCollection<DownloadIntent> intents,
        long? destinationFolderId,
        CancellationToken cancellationToken)
    {
        if (intents.Count == 0)
        {
            return Task.FromResult(0);
        }

        var watchTracks = intents
            .Select(intent =>
            {
                var trackId = ResolveIntentTrackId(intent);
                if (string.IsNullOrWhiteSpace(trackId))
                {
                    return null;
                }

                return new WatchIntentTrack(trackId, intent.Isrc, intent);
            })
            .Where(static track => track is not null)
            .Select(static track => track!)
            .ToList();

        var sourceLabel = BuildQueueSourceLabel("Apple Music", collectionType, collectionName);
        return QueueWatchIntentTracksAsync(
            watchTracks,
            destinationFolderId,
            BuildQueueWatchOptions(sourceLabel, null, null),
            cancellationToken);
    }

    private async Task<int> QueueSpotifyTracksAsync(
        IReadOnlyCollection<SpotifyTrackSummary> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return 0;
        }

        var watchTracks = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .Select(track =>
            {
                var trackId = track.Id.Trim();
                var intent = new DownloadIntent
                {
                    SourceService = SpotifySource,
                    SourceUrl = BuildSpotifyTrackUrl(trackId, track.SourceUrl),
                    SpotifyId = trackId,
                    Isrc = track.Isrc ?? string.Empty,
                    Title = track.Name ?? string.Empty,
                    Artist = track.Artists ?? string.Empty,
                    Album = track.Album ?? string.Empty,
                    AlbumArtist = track.AlbumArtist ?? track.Artists ?? string.Empty,
                    Cover = track.ImageUrl ?? string.Empty,
                    DurationMs = track.DurationMs ?? 0,
                    Position = track.TrackNumber ?? 0,
                    ReleaseDate = track.ReleaseDate ?? string.Empty,
                    TrackNumber = track.TrackNumber ?? 0,
                    DiscNumber = track.DiscNumber ?? 0,
                    TrackTotal = track.TrackTotal ?? 0,
                    Explicit = track.Explicit,
                    Danceability = track.Danceability,
                    Energy = track.Energy,
                    Valence = track.Valence,
                    Acousticness = track.Acousticness,
                    Instrumentalness = track.Instrumentalness,
                    Speechiness = track.Speechiness,
                    Loudness = track.Loudness,
                    Tempo = track.Tempo,
                    TimeSignature = track.TimeSignature,
                    Liveness = track.Liveness,
                    Label = track.Label ?? string.Empty,
                    Genres = track.Genres?
                        .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                        .Select(static genre => genre.Trim())
                        .ToList() ?? new List<string>()
                };
                return new WatchIntentTrack(trackId, track.Isrc, intent);
            })
            .ToList();

        return await QueueWatchIntentTracksAsync(
            watchTracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private async Task<int> QueueDeezerTracksAsync(
        IReadOnlyCollection<GwTrack> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return 0;
        }

        var watchTracks = tracks
            .Where(track => track.SngId > 0)
            .Select(track =>
            {
                var trackId = track.SngId.ToString();
                var durationMs = track.Duration > 0 ? track.Duration * 1000 : 0;
                var intent = new DownloadIntent
                {
                    SourceService = DeezerSource,
                    SourceUrl = BuildDeezerTrackUrl(trackId),
                    DeezerId = trackId,
                    Isrc = track.Isrc ?? string.Empty,
                    Title = track.SngTitle ?? string.Empty,
                    Artist = track.ArtName ?? string.Empty,
                    Album = track.AlbTitle ?? string.Empty,
                    AlbumArtist = track.ArtName ?? string.Empty,
                    Cover = BuildDeezerCoverUrl(track.AlbPicture),
                    DurationMs = durationMs,
                    Position = track.Position > 0 ? track.Position : track.TrackNumber
                };
                return new WatchIntentTrack(trackId, track.Isrc, intent);
            })
            .ToList();

        return await QueueWatchIntentTracksAsync(
            watchTracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private Task<int> QueueAppleTracksAsync(
        IReadOnlyCollection<WatchIntentTrack> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        return QueueWatchIntentTracksAsync(
            tracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private Task<int> QueueBoomplayTracksAsync(
        IReadOnlyCollection<WatchIntentTrack> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        return QueueWatchIntentTracksAsync(
            tracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private Task<int> QueueSmartTracklistTracksAsync(
        IReadOnlyCollection<WatchIntentTrack> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        return QueueWatchIntentTracksAsync(
            tracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private Task<int> QueueRecommendationTracksAsync(
        IReadOnlyCollection<RecommendationTrackDto> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return Task.FromResult(0);
        }

        var watchTracks = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .Select(track =>
            {
                var trackId = track.Id.Trim();
                var artist = track.Artist?.Name ?? string.Empty;
                var intent = new DownloadIntent
                {
                    SourceService = DeezerSource,
                    SourceUrl = BuildDeezerTrackUrl(trackId),
                    DeezerId = trackId,
                    Isrc = track.Isrc ?? string.Empty,
                    Title = track.Title ?? string.Empty,
                    Artist = artist,
                    Album = track.Album?.Title ?? string.Empty,
                    AlbumArtist = artist,
                    Cover = track.Album?.CoverMedium ?? string.Empty,
                    DurationMs = track.Duration > 0 ? track.Duration * 1000 : 0,
                    Position = track.TrackPosition > 0 ? track.TrackPosition : 0,
                    TrackNumber = track.TrackPosition > 0 ? track.TrackPosition : 0
                };
                return new WatchIntentTrack(trackId, track.Isrc, intent);
            })
            .ToList();

        return QueueWatchIntentTracksAsync(
            watchTracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private static string BuildQueueSourceLabel(string defaultLabel, string collectionType, string collectionName)
    {
        var normalizedType = (collectionType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedType))
        {
            return defaultLabel;
        }

        var normalizedName = (collectionName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalizedName)
            ? $"{defaultLabel} {normalizedType}"
            : $"{defaultLabel} {normalizedType}:{normalizedName}";
    }

    private static QueueWatchOptions BuildQueueWatchOptions(
        string sourceLabel,
        string? watchlistSource,
        string? watchlistPlaylistId,
        string? preferredEngine = null,
        string? downloadVariantMode = null,
        IReadOnlyList<PlaylistTrackRoutingRule>? routingRules = null,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules = null)
    {
        return new QueueWatchOptions(
            sourceLabel,
            watchlistSource,
            watchlistPlaylistId,
            preferredEngine,
            downloadVariantMode,
            routingRules,
            blockRules);
    }

    private static long? ResolveRoutingFolderId(DownloadIntent intent, IReadOnlyList<PlaylistTrackRoutingRule>? rules, long? defaultFolderId)
    {
        if (rules is null || rules.Count == 0)
        {
            return defaultFolderId;
        }

        var matchedRule = rules
            .OrderBy(static r => r.Order)
            .FirstOrDefault(rule => RuleMatches(intent, rule.ConditionField, rule.ConditionOperator, rule.ConditionValue));

        return matchedRule?.DestinationFolderId ?? defaultFolderId;
    }

    private static bool ShouldBlockTrack(DownloadIntent intent, IReadOnlyList<PlaylistTrackBlockRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        return rules
            .OrderBy(static r => r.Order)
            .Any(rule => RuleMatches(intent, rule.ConditionField, rule.ConditionOperator, rule.ConditionValue));
    }

    private static bool RuleMatches(
        DownloadIntent intent,
        string conditionField,
        string conditionOperator,
        string conditionValue)
    {
        return conditionField switch
        {
            "artist" => EvalStringCondition(intent.Artist, conditionOperator, conditionValue),
            "title" => EvalStringCondition(intent.Title, conditionOperator, conditionValue),
            AlbumField => EvalStringCondition(intent.Album, conditionOperator, conditionValue),
            "genre" => EvalGenreCondition(intent.Genres, conditionOperator, conditionValue),
            "explicit" => conditionOperator == "is_true" ? (intent.Explicit == true) : (intent.Explicit != true),
            "year" => EvalYearCondition(intent.ReleaseDate, conditionOperator, conditionValue),
            _ => false
        };
    }

    private static bool EvalStringCondition(string value, string op, string conditionValue) => op switch
    {
        "contains" => value.Contains(conditionValue, StringComparison.OrdinalIgnoreCase),
        "equals" => string.Equals(value, conditionValue, StringComparison.OrdinalIgnoreCase),
        "starts_with" => value.StartsWith(conditionValue, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool EvalGenreCondition(IReadOnlyCollection<string>? genres, string op, string conditionValue)
    {
        if (genres is null || genres.Count == 0)
        {
            return false;
        }

        var normalizedCondition = (conditionValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedCondition))
        {
            return false;
        }

        return genres
            .Where(static genre => !string.IsNullOrWhiteSpace(genre))
            .Select(static genre => genre.Trim())
            .Any(genre => EvalStringCondition(genre, op, normalizedCondition));
    }

    private static bool EvalYearCondition(string? releaseDate, string op, string conditionValue)
    {
        if (!TryParseReleaseYear(releaseDate, out var trackYear)
            || !int.TryParse((conditionValue ?? string.Empty).Trim(), out var ruleYear))
        {
            return false;
        }

        return op switch
        {
            "gte" => trackYear >= ruleYear,
            "lte" => trackYear <= ruleYear,
            _ => trackYear == ruleYear
        };
    }

    private static bool TryParseReleaseYear(string? releaseDate, out int year)
    {
        year = 0;
        var value = (releaseDate ?? string.Empty).Trim();
        if (value.Length < 4)
        {
            return false;
        }

        return int.TryParse(value[..4], out year);
    }

    private static string? NormalizePreferredEngine(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        var normalized = engine.Trim().ToLowerInvariant();
        return normalized is "auto" or DeezerSource or AppleSource or "qobuz" or "tidal" or "amazon"
            ? normalized
            : null;
    }

    private static string NormalizeDownloadVariantMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? "standard"
            : mode.Trim().ToLowerInvariant();

        return normalized is "dual_quality" or "atmos_only"
            ? normalized
            : "standard";
    }

    private DownloadIntent CreateAtmosOnlyIntent(DownloadIntent sourceIntent, long? destinationFolderId)
    {
        var atmosDestinationFolderId = _settingsService.LoadSettings().MultiQuality?.SecondaryDestinationFolderId
            ?? destinationFolderId;

        return new DownloadIntent
        {
            SourceService = sourceIntent.SourceService,
            SourceUrl = sourceIntent.SourceUrl,
            SpotifyId = sourceIntent.SpotifyId,
            DeezerId = sourceIntent.DeezerId,
            DeezerAlbumId = sourceIntent.DeezerAlbumId,
            DeezerArtistId = sourceIntent.DeezerArtistId,
            Isrc = sourceIntent.Isrc,
            Title = sourceIntent.Title,
            Artist = sourceIntent.Artist,
            Album = sourceIntent.Album,
            AlbumArtist = sourceIntent.AlbumArtist,
            Cover = sourceIntent.Cover,
            DurationMs = sourceIntent.DurationMs,
            Position = sourceIntent.Position,
            Genres = new List<string>(sourceIntent.Genres ?? []),
            Label = sourceIntent.Label,
            Copyright = sourceIntent.Copyright,
            Explicit = sourceIntent.Explicit,
            Composer = sourceIntent.Composer,
            ReleaseDate = sourceIntent.ReleaseDate,
            TrackNumber = sourceIntent.TrackNumber,
            DiscNumber = sourceIntent.DiscNumber,
            TrackTotal = sourceIntent.TrackTotal,
            DiscTotal = sourceIntent.DiscTotal,
            Url = sourceIntent.Url,
            Barcode = sourceIntent.Barcode,
            PreferredEngine = AppleSource,
            Quality = "atmos",
            ContentType = DownloadContentTypes.Atmos,
            DestinationFolderId = atmosDestinationFolderId,
            SecondaryDestinationFolderId = null,
            AppleId = sourceIntent.AppleId,
            WatchlistSource = sourceIntent.WatchlistSource,
            WatchlistPlaylistId = sourceIntent.WatchlistPlaylistId,
            WatchlistTrackId = sourceIntent.WatchlistTrackId,
            HasAtmos = sourceIntent.HasAtmos,
            HasAppleDigitalMaster = sourceIntent.HasAppleDigitalMaster,
            Danceability = sourceIntent.Danceability,
            Energy = sourceIntent.Energy,
            Valence = sourceIntent.Valence,
            Acousticness = sourceIntent.Acousticness,
            Instrumentalness = sourceIntent.Instrumentalness,
            Speechiness = sourceIntent.Speechiness,
            Loudness = sourceIntent.Loudness,
            Tempo = sourceIntent.Tempo,
            TimeSignature = sourceIntent.TimeSignature,
            Liveness = sourceIntent.Liveness,
            MusicKey = sourceIntent.MusicKey,
            AllowQualityUpgrade = sourceIntent.AllowQualityUpgrade
        };
    }

    private async Task<int> QueueWatchIntentTracksAsync(
        IReadOnlyCollection<WatchIntentTrack> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return 0;
        }

        using var scope = _serviceProvider.CreateScope();
        var intentService = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
        var normalizedPreferredEngine = NormalizePreferredEngine(options.PreferredEngine);
        var normalizedDownloadVariantMode = NormalizeDownloadVariantMode(options.DownloadVariantMode);

        var queuedCount = 0;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var intent = track.Intent;
            if (await HandleBlockedWatchIntentAsync(intent, track, options, cancellationToken))
            {
                continue;
            }

            intent = PrepareWatchIntent(
                intent,
                track.TrackId,
                options,
                destinationFolderId,
                normalizedDownloadVariantMode,
                normalizedPreferredEngine);

            var result = await TryQueuePrimaryIntentAsync(
                intentService,
                intent,
                options.SourceLabel,
                track.TrackId,
                cancellationToken);
            if (result is null)
            {
                continue;
            }

            queuedCount += await HandleQueuedWatchIntentResultAsync(
                intentService,
                result,
                track,
                intent,
                options,
                normalizedDownloadVariantMode,
                cancellationToken);
        }

        return queuedCount;
    }

    private async Task<int> HandleQueuedWatchIntentResultAsync(
        DownloadIntentService intentService,
        DownloadIntentResult result,
        WatchIntentTrack track,
        DownloadIntent intent,
        QueueWatchOptions options,
        string normalizedDownloadVariantMode,
        CancellationToken cancellationToken)
    {
        var queuedCount = 0;
        if (result.Success)
        {
            queuedCount++;
            queuedCount += await TryQueueAtmosIntentAsync(
                intentService,
                normalizedDownloadVariantMode,
                intent,
                options.SourceLabel,
                track.TrackId,
                afterPrimarySkip: false,
                cancellationToken);
            return queuedCount;
        }

        if (ShouldMarkWatchTrackAsCompleted(result))
        {
            if (ShouldPersistBlockedTrackIgnore(result))
            {
                await TryPersistWatchTrackIgnoreAsync(
                    options.WatchlistSource,
                    options.WatchlistPlaylistId,
                    track,
                    cancellationToken);
            }
            queuedCount += await TryQueueAtmosIntentAsync(
                intentService,
                normalizedDownloadVariantMode,
                intent,
                options.SourceLabel,
                track.TrackId,
                afterPrimarySkip: true,
                cancellationToken);
            await TryMarkWatchTrackCompletedAsync(
                options.WatchlistSource,
                options.WatchlistPlaylistId,
                track.TrackId,
                cancellationToken);
        }

        return queuedCount;
    }

    private async Task<bool> HandleBlockedWatchIntentAsync(
        DownloadIntent intent,
        WatchIntentTrack track,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (!ShouldBlockTrack(intent, options.BlockRules))
        {
            return false;
        }

        _logger.LogDebug(
            "{Source} watch skipped blocked track {TrackId} ({Title} - {Artist}).",
            options.SourceLabel,
            track.TrackId,
            intent.Title,
            intent.Artist);
        await TryPersistWatchTrackIgnoreAsync(
            options.WatchlistSource,
            options.WatchlistPlaylistId,
            track,
            cancellationToken);
        await TryMarkWatchTrackCompletedAsync(
            options.WatchlistSource,
            options.WatchlistPlaylistId,
            track.TrackId,
            cancellationToken);
        return true;
    }

    private DownloadIntent PrepareWatchIntent(
        DownloadIntent intent,
        string trackId,
        QueueWatchOptions options,
        long? destinationFolderId,
        string normalizedDownloadVariantMode,
        string? normalizedPreferredEngine)
    {
        intent.DestinationFolderId = ResolveRoutingFolderId(intent, options.RoutingRules, destinationFolderId);
        if (normalizedDownloadVariantMode == "atmos_only")
        {
            intent = CreateAtmosOnlyIntent(intent, intent.DestinationFolderId);
        }
        else if (!string.IsNullOrWhiteSpace(normalizedPreferredEngine))
        {
            intent.PreferredEngine = normalizedPreferredEngine;
        }

        if (HasWatchlistContext(options.WatchlistSource, options.WatchlistPlaylistId))
        {
            intent.WatchlistSource = options.WatchlistSource!;
            intent.WatchlistPlaylistId = options.WatchlistPlaylistId!;
            intent.WatchlistTrackId = trackId;
        }

        return intent;
    }

    private async Task<DownloadIntentResult?> TryQueuePrimaryIntentAsync(
        DownloadIntentService intentService,
        DownloadIntent intent,
        string sourceLabel,
        string trackId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await intentService.EnqueueAsync(intent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Source} watch queue failed for track {TrackId}", sourceLabel, trackId);
            return null;
        }
    }

    private async Task<int> TryQueueAtmosIntentAsync(
        DownloadIntentService intentService,
        string normalizedDownloadVariantMode,
        DownloadIntent baseIntent,
        string sourceLabel,
        string trackId,
        bool afterPrimarySkip,
        CancellationToken cancellationToken)
    {
        if (normalizedDownloadVariantMode != "dual_quality")
        {
            return 0;
        }

        var atmosIntent = CreateAtmosOnlyIntent(baseIntent, baseIntent.DestinationFolderId);
        try
        {
            var atmosResult = await intentService.EnqueueAsync(atmosIntent, cancellationToken);
            return atmosResult.Success ? 1 : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var messageSuffix = afterPrimarySkip ? " after primary skip" : string.Empty;
            _logger.LogWarning(
                ex,
                "{Source} watch Atmos queue failed{Suffix} for track {TrackId}",
                sourceLabel,
                messageSuffix,
                trackId);
            return 0;
        }
    }

    private static bool HasWatchlistContext(string? watchlistSource, string? watchlistPlaylistId)
    {
        return !string.IsNullOrWhiteSpace(watchlistSource)
               && !string.IsNullOrWhiteSpace(watchlistPlaylistId);
    }

    private async Task TryMarkWatchTrackCompletedAsync(
        string? watchlistSource,
        string? watchlistPlaylistId,
        string trackId,
        CancellationToken cancellationToken)
    {
        if (!HasWatchlistContext(watchlistSource, watchlistPlaylistId)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        try
        {
            await _libraryRepository.UpdatePlaylistWatchTrackStatusAsync(
                watchlistSource!,
                watchlistPlaylistId!,
                trackId,
                "completed",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to mark watch track as completed: {Source}:{PlaylistId}:{TrackId}", watchlistSource, watchlistPlaylistId, trackId);
        }
    }

    private async Task TryPersistWatchTrackIgnoreAsync(
        string? watchlistSource,
        string? watchlistPlaylistId,
        WatchIntentTrack track,
        CancellationToken cancellationToken)
    {
        if (!HasWatchlistContext(watchlistSource, watchlistPlaylistId)
            || string.IsNullOrWhiteSpace(track.TrackId))
        {
            return;
        }

        try
        {
            await _libraryRepository.AddPlaylistWatchIgnoredTracksAsync(
                watchlistSource!,
                watchlistPlaylistId!,
                new List<PlaylistWatchIgnoreInsert> { new(track.TrackId, track.Isrc) },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Failed to persist watch ignore entry: {Source}:{PlaylistId}:{TrackId}",
                watchlistSource,
                watchlistPlaylistId,
                track.TrackId);
        }
    }

    private static bool ShouldMarkWatchTrackAsCompleted(DownloadIntentResult result)
    {
        if (result?.SkipReasonCodes == null || result.SkipReasonCodes.Count == 0)
        {
            return false;
        }

        foreach (var reasonCode in result.SkipReasonCodes)
        {
            switch (reasonCode?.Trim().ToLowerInvariant())
            {
                case "library_duplicate":
                case "library_quality_not_higher":
                case "queue_duplicate":
                case "queue_insert_ignored":
                case "queue_quality_not_higher":
                case "queue_upgrade_in_progress":
                case "blocklist_match":
                    return true;
            }
        }

        return false;
    }

    private static bool ShouldPersistBlockedTrackIgnore(DownloadIntentResult result)
    {
        if (result?.SkipReasonCodes == null || result.SkipReasonCodes.Count == 0)
        {
            return false;
        }

        return result.SkipReasonCodes.Any(
            reasonCode => string.Equals(reasonCode?.Trim(), "blocklist_match", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSpotifyTrackUrl(string trackId, string? sourceUrl)
    {
        return string.IsNullOrWhiteSpace(sourceUrl)
            ? $"https://open.spotify.com/track/{trackId}"
            : sourceUrl;
    }

    private static string BuildDeezerTrackUrl(string trackId)
    {
        return $"https://www.deezer.com/track/{trackId}";
    }

    private static string BuildDeezerCoverUrl(string? coverId)
    {
        if (string.IsNullOrWhiteSpace(coverId))
        {
            return string.Empty;
        }

        return $"https://cdns-images.dzcdn.net/images/cover/{coverId}/1000x1000-000000-80-0-0.jpg";
    }

    private static string? ResolveIntentTrackId(DownloadIntent intent)
    {
        if (!string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            return intent.SpotifyId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            return intent.DeezerId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(intent.AppleId))
        {
            return intent.AppleId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(intent.SourceUrl))
        {
            return intent.SourceUrl.Trim();
        }

        return null;
    }

    private static bool TryParseRecommendationLibraryId(string? stationId, out long libraryId)
    {
        libraryId = 0;
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return false;
        }

        var value = stationId.Trim();
        if (!value.StartsWith("daily-rotation:l", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        var libraryPart = parts[1];
        if (!libraryPart.StartsWith("l", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(libraryPart[1..], out libraryId) && libraryId > 0;
    }

    private static string NormalizeWatchSource(string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "smarttracks" => SmartTracklistSource,
            "recommendation" => RecommendationsSource,
            "itunes" => AppleSource,
            "applemusic" => AppleSource,
            _ => string.IsNullOrWhiteSpace(normalized) ? DeezerSource : normalized
        };
    }

    private static bool TryGetSpotifyArtistTopTracksSourceId(string? sourceId, out string artistId)
    {
        artistId = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        const string prefix = "artist-top:";
        if (!sourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        artistId = sourceId.Substring(prefix.Length).Trim();
        return !string.IsNullOrWhiteSpace(artistId);
    }

    private static bool IsSpotifyHomeTrendingSourceId(string? sourceId)
    {
        return !string.IsNullOrWhiteSpace(sourceId)
               && string.Equals(
                   sourceId.Trim(),
                   SpotifyHomeTrendingSourceId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSpotifyTopTracksSnapshot(IEnumerable<string> trackIds)
    {
        var materialized = trackIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray();
        if (materialized.Length == 0)
        {
            return string.Empty;
        }

        var payload = string.Join("|", materialized);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static string BuildTrackIdSnapshot(IEnumerable<string> trackIds)
    {
        return BuildSpotifyTopTracksSnapshot(trackIds);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return bool.TrueString;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return bool.FalseString;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return GetJsonStringFromObject(value);
        }

        return null;
    }

    private static string? GetJsonStringFromObject(JsonElement value)
    {
        foreach (var propertyName in JsonStringObjectPropertyNames)
        {
            if (value.TryGetProperty(propertyName, out var candidate)
                && candidate.ValueKind == JsonValueKind.String)
            {
                return candidate.GetString();
            }
        }
        return null;
    }

    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static List<string> ReadJsonStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToList();
    }

    private static string? ResolveAppleArtworkUrl(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("artwork", out var artwork)
            || artwork.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!artwork.TryGetProperty("url", out var urlValue)
            || urlValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var url = urlValue.GetString();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var width = GetJsonInt(artwork, "width") ?? 1000;
        var height = GetJsonInt(artwork, "height") ?? 1000;

        return url
            .Replace("{w}", width.ToString(), StringComparison.Ordinal)
            .Replace("{h}", height.ToString(), StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);
    }

    private sealed record QueueWatchOptions(
        string SourceLabel,
        string? WatchlistSource,
        string? WatchlistPlaylistId,
        string? PreferredEngine,
        string? DownloadVariantMode,
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules);

    private sealed record WatchIntentTrack(string TrackId, string? Isrc, DownloadIntent Intent);

    private sealed record ApplePlaylistWatchData(
        string Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        IReadOnlyCollection<WatchIntentTrack> Tracks);

    private sealed record BoomplayPlaylistWatchData(
        string Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        IReadOnlyCollection<WatchIntentTrack> Tracks);

    private sealed record SmartTracklistWatchData(
        string Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        IReadOnlyCollection<WatchIntentTrack> Tracks);
}
