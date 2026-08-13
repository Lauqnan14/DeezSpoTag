using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Library;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistSyncService
{
    private const int MaxUploadedMergeArtworkBytes = 8 * 1024 * 1024;
    private const string PlaylistNotAvailableMessage = "Playlist not available.";
    private sealed record PlexConnection(string Url, string Token, string MachineIdentifier);
    private sealed record JellyfinConnection(string Url, string ApiKey, string UserId);
    private sealed record NavidromeConnection(string Url, string Username, string Password);

    private sealed record SyncTrackSummary(
        string SourceTrackId,
        string? Isrc,
        string Name,
        string Artists,
        string Album,
        string? ReleaseDate,
        bool? Explicit,
        IReadOnlyList<string> Genres,
        int? DurationMs,
        string? IdentitySource = null,
        string? IdentityTrackId = null);

    private sealed record SyncMatchSummary(
        List<string> TargetIds,
        List<PlaylistWatchTargetMembership> Memberships,
        int SourceTracks,
        int LocalMatches,
        int TargetMatches,
        int MissingTracks,
        int MetadataMatches,
        int SearchMatches);

    public sealed record GeneratedLocalPlaylistSyncRequest(
        string PlaylistName,
        string? Description,
        string StableTitlePrefix,
        IReadOnlyList<MixTrackDto> Tracks,
        IReadOnlyList<string> TargetServices,
        string? ArtworkFilePath = null,
        string? ArtworkContentType = null,
        string? ArtworkUrl = null,
        string? AnimatedArtworkFilePath = null,
        string? AnimatedArtworkContentType = null);

    public sealed record GeneratedLocalPlaylistTargetResult(
        string Service,
        bool Success,
        string Message,
        string? PlaylistId = null,
        int SourceTracks = 0,
        int LocalMatches = 0,
        int TargetMatches = 0,
        int MissingTracks = 0);

    public sealed record GeneratedLocalPlaylistSyncResult(
        bool Success,
        string Message,
        IReadOnlyList<GeneratedLocalPlaylistTargetResult> Targets)
    {
        public string? FirstPlaylistId => Targets.FirstOrDefault(static target => !string.IsNullOrWhiteSpace(target.PlaylistId))?.PlaylistId;
    }

    public sealed record SpotifyRecommendationPlaylistSyncRequest(
        string PlaylistId,
        string? Name,
        string? Description,
        string? ImageUrl,
        bool Monitor);

    public sealed record SpotifyRecommendationPlaylistSyncResult(
        bool Success,
        string Message,
        string PlaylistName,
        string PlaylistId,
        string? TargetPlaylistId,
        int SourceTracks,
        int LocalMatches,
        int TargetMatches,
        int MissingTracks,
        bool Monitored);

    private const string SpotifySource = "spotify";
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string NavidromeService = "navidrome";
    private const string JellyfinPlaylistMoveCapability = "playlist_move";
    private const string NavidromeNativePlaylistPutCapability = "native_playlist_put";
    private const string SyncModeMirror = "mirror";
    private const string SyncModeAppend = "append";
    private const int DurationToleranceMs = 2000;
    private const string NoTargetServerSelectedMessage = "No target server selected.";
    private const string UnsupportedPlaylistSyncTargetMessage = "Unsupported playlist sync target.";
    private const string PlexNotConfiguredMessage = "Plex is not configured.";
    private const string JellyfinNotConfiguredMessage = "Jellyfin is not configured.";
    private const string NavidromeNotConfiguredMessage = "Navidrome is not configured.";
    private static readonly TimeSpan IdentityRefreshThrottle = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastIdentityRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly LibraryRepository _libraryRepository;
    private readonly ILocalTrackAmbiguityResolver _localIdentityResolver;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly PlatformAuthService _authService;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly MediaServerLibraryRefreshService _mediaServerRefreshService;
    private readonly SharedIdentityResolver _sharedIdentityResolver;
    private readonly CrossDeviceSyncService? _crossDeviceSyncService;
    private readonly WatchlistRunSignal? _runSignal;
    private readonly ILogger<PlaylistSyncService> _logger;

    public PlaylistSyncService(PlaylistSyncDependencies dependencies)
    {
        _libraryRepository = dependencies.LibraryRepository;
        _localIdentityResolver = dependencies.LocalIdentityResolver;
        _spotifyMetadataService = dependencies.SpotifyMetadataService;
        _plexApiClient = dependencies.PlexApiClient;
        _jellyfinApiClient = dependencies.JellyfinApiClient;
        _navidromeApiClient = dependencies.NavidromeApiClient;
        _authService = dependencies.AuthService;
        _playlistVisualService = dependencies.PlaylistVisualService;
        _mediaServerRefreshService = dependencies.MediaServerRefreshService;
        _sharedIdentityResolver = dependencies.SharedIdentityResolver;
        _crossDeviceSyncService = dependencies.CrossDeviceSyncService;
        _runSignal = dependencies.WatchlistRunSignal;
        _logger = dependencies.Logger;
    }

    public sealed class PlaylistSyncDependencies
    {
        public required LibraryRepository LibraryRepository { get; init; }
        public required ILocalTrackAmbiguityResolver LocalIdentityResolver { get; init; }
        public required SpotifyMetadataService SpotifyMetadataService { get; init; }
        public required PlexApiClient PlexApiClient { get; init; }
        public required JellyfinApiClient JellyfinApiClient { get; init; }
        public required NavidromeApiClient NavidromeApiClient { get; init; }
        public required PlatformAuthService AuthService { get; init; }
        public required PlaylistVisualService PlaylistVisualService { get; init; }
        public required MediaServerLibraryRefreshService MediaServerRefreshService { get; init; }
        public required SharedIdentityResolver SharedIdentityResolver { get; init; }
        public CrossDeviceSyncService? CrossDeviceSyncService { get; init; }
        public WatchlistRunSignal? WatchlistRunSignal { get; init; }
        public required ILogger<PlaylistSyncService> Logger { get; init; }
    }

    public Task<PlaylistSyncResult> SyncSpotifyPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        bool force,
        CancellationToken cancellationToken)
    {
        return SyncPlaylistAsync(playlist, preference, trackCandidates: null, force, cancellationToken);
    }

    public async Task<SpotifyRecommendationPlaylistSyncResult> SyncSpotifyRecommendationPlaylistToNavidromeAsync(
        SpotifyRecommendationPlaylistSyncRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var playlistId = (request.PlaylistId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return new SpotifyRecommendationPlaylistSyncResult(
                false,
                PlaylistNotAvailableMessage,
                "Spotify Playlist",
                string.Empty,
                null,
                0,
                0,
                0,
                0,
                request.Monitor);
        }

        var playlistName = string.IsNullOrWhiteSpace(request.Name)
            ? "Spotify Playlist"
            : request.Name.Trim();
        var now = DateTimeOffset.UtcNow;
        var sourceUrl = $"https://open.spotify.com/playlist/{playlistId}";
        var playlist = new PlaylistWatchlistDto(
            0,
            SpotifySource,
            playlistId,
            playlistName,
            request.ImageUrl,
            request.Description,
            null,
            now,
            OwnerName: "Spotify",
            SourceUrl: sourceUrl);

        if (!string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            await _playlistVisualService.InspectSourceArtworkAsync(
                SpotifySource,
                playlistId,
                playlistName,
                request.ImageUrl,
                activateChangedArtwork: true,
                authoritativeRemoval: false,
                cancellationToken);
        }

        var preference = new PlaylistWatchPreferenceDto(
            SpotifySource,
            playlistId,
            DestinationFolderId: null,
            Service: NavidromeService,
            SyncTargets: new[] { NavidromeService },
            PreferredEngine: null,
            DownloadEngineOrder: null,
            DownloadVariantMode: null,
            SyncMode: SyncModeMirror,
            UpdateArtwork: true,
            ReuseSavedArtwork: false,
            CreatedAt: now,
            UpdatedAt: now);

        if (request.Monitor)
        {
            var savedPlaylist = await _libraryRepository.AddPlaylistWatchlistAsync(
                SpotifySource,
                playlistId,
                new PlaylistWatchlistMetadataInput(
                    playlistName,
                    request.ImageUrl,
                    request.Description,
                    TrackCount: null,
                    OwnerName: "Spotify",
                    SourceUrl: sourceUrl),
                cancellationToken);

            if (savedPlaylist is null)
            {
                return new SpotifyRecommendationPlaylistSyncResult(
                    false,
                    "Failed to monitor Spotify playlist.",
                    playlistName,
                    playlistId,
                    null,
                    0,
                    0,
                    0,
                    0,
                    true);
            }

            playlist = savedPlaylist;
            preference = await _libraryRepository.UpsertPlaylistWatchPreferenceAsync(
                new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                    SpotifySource,
                    playlistId,
                    DestinationFolderId: null,
                    Service: NavidromeService,
                    SyncTargets: new[] { NavidromeService },
                    PreferredEngine: null,
                    DownloadEngineOrder: null,
                    DownloadVariantMode: null,
                    SyncMode: SyncModeMirror,
                    UpdateArtwork: true,
                    ReuseSavedArtwork: false),
                cancellationToken) ?? preference;
        }

        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates: null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return new SpotifyRecommendationPlaylistSyncResult(
                false,
                loadResult.ErrorMessage,
                playlistName,
                playlistId,
                null,
                0,
                0,
                0,
                0,
                request.Monitor);
        }

        var tracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            loadResult.Tracks,
            cancellationToken);
        if (tracks.Count == 0)
        {
            return new SpotifyRecommendationPlaylistSyncResult(
                false,
                "No eligible tracks after blocked/ignored filtering.",
                playlistName,
                playlistId,
                null,
                loadResult.Tracks.Count,
                0,
                0,
                loadResult.Tracks.Count,
                request.Monitor);
        }

        var result = await SyncPlaylistToTargetAsync(
            NavidromeService,
            playlist,
            preference,
            tracks,
            cancellationToken);

        if (request.Monitor)
        {
            _runSignal?.Request(WatchlistWakeReason.Reconciliation | WatchlistWakeReason.TargetSync);
        }

        return new SpotifyRecommendationPlaylistSyncResult(
            result.Success,
            result.Message,
            playlistName,
            playlistId,
            result.PlaylistId,
            result.SourceTracks,
            result.LocalMatches,
            result.TargetMatches,
            result.MissingTracks,
            request.Monitor);
    }

    public async Task<GeneratedLocalPlaylistSyncResult> SyncGeneratedLocalPlaylistAsync(
        GeneratedLocalPlaylistSyncRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = NormalizeGeneratedTargetServices(request.TargetServices);
        if (services.Count == 0)
        {
            return new GeneratedLocalPlaylistSyncResult(false, NoTargetServerSelectedMessage, Array.Empty<GeneratedLocalPlaylistTargetResult>());
        }

        var tracks = request.Tracks
            .Where(static track => track.TrackId > 0)
            .Select(static track => new SyncTrackSummary(
                track.TrackId.ToString(CultureInfo.InvariantCulture),
                Isrc: null,
                track.Title,
                track.ArtistName,
                track.AlbumTitle,
                ReleaseDate: null,
                Explicit: null,
                Genres: Array.Empty<string>(),
                track.DurationMs))
            .ToList();
        var orderedTrackIds = request.Tracks
            .Where(static track => track.TrackId > 0)
            .Select(static track => track.TrackId)
            .ToList();
        if (tracks.Count == 0)
        {
            var failed = new GeneratedLocalPlaylistTargetResult(
                "local",
                false,
                "Generated playlist has no local tracks to sync.");
            return new GeneratedLocalPlaylistSyncResult(false, failed.Message, new[] { failed });
        }

        var results = new List<GeneratedLocalPlaylistTargetResult>(services.Count);
        foreach (var service in services)
        {
            results.Add(await SyncGeneratedLocalPlaylistToTargetAsync(service, request, tracks, orderedTrackIds, cancellationToken));
        }

        var successful = results.Where(static result => result.Success).ToList();
        var message = successful.Count == 0
            ? string.Join(" ", results.Select(static result => result.Message).Where(static message => !string.IsNullOrWhiteSpace(message)))
            : string.Join(" ", results.Select(static result => result.Message).Where(static message => !string.IsNullOrWhiteSpace(message)));
        return new GeneratedLocalPlaylistSyncResult(successful.Count > 0, message, results);
    }

    private static List<string> NormalizeGeneratedTargetServices(IReadOnlyList<string>? targetServices)
    {
        var normalized = new List<string>();
        foreach (var service in targetServices ?? Array.Empty<string>())
        {
            var value = (service ?? string.Empty).Trim().ToLowerInvariant();
            if (value is PlexService or JellyfinService or NavidromeService
                && !normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToTargetAsync(
        string service,
        GeneratedLocalPlaylistSyncRequest request,
        IReadOnlyList<SyncTrackSummary> tracks,
        List<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        try
        {
            return service switch
            {
                PlexService => await SyncGeneratedLocalPlaylistToPlexAsync(request, tracks, orderedTrackIds, cancellationToken),
                JellyfinService => await SyncGeneratedLocalPlaylistToJellyfinAsync(request, tracks, orderedTrackIds, cancellationToken),
                NavidromeService => await SyncGeneratedLocalPlaylistToNavidromeAsync(request, tracks, orderedTrackIds, cancellationToken),
                _ => new GeneratedLocalPlaylistTargetResult(service, false, UnsupportedPlaylistSyncTargetMessage)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Generated local playlist sync failed for target {TargetService}.",
                service);
            return new GeneratedLocalPlaylistTargetResult(
                service,
                false,
                $"{FormatGeneratedServiceLabel(service)} failed: {ex.Message}");
        }
    }

    private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToPlexAsync(
        GeneratedLocalPlaylistSyncRequest request,
        IReadOnlyList<SyncTrackSummary> tracks,
        List<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError is not null || plex is null)
        {
            return new GeneratedLocalPlaylistTargetResult(PlexService, false, configurationError?.Message ?? PlexNotConfiguredMessage);
        }

        var matchSummary = await ResolvePlexRatingKeysAsync(plex, tracks, orderedTrackIds, cancellationToken);
        if (matchSummary.TargetIds.Count == 0)
        {
            return BuildGeneratedTargetResult(PlexService, null, "Plex skipped: no target tracks resolved.", matchSummary);
        }

        var upsert = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            request.PlaylistName,
            matchSummary.TargetIds,
            options: new PlexApiClient.PlaylistUpsertOptions(
                ExistingTitlePrefix: request.StableTitlePrefix),
            cancellationToken: cancellationToken);
        var playlistId = upsert.PlaylistId;
        if (string.IsNullOrWhiteSpace(playlistId) || !upsert.Complete)
        {
            return BuildGeneratedTargetResult(PlexService, null, "Plex failed to create or update playlist.", matchSummary);
        }

        await _plexApiClient.UpdatePlaylistMetadataAsync(
            plex.Url,
            plex.Token,
            playlistId,
            request.PlaylistName,
            request.Description,
            cancellationToken);
        var artworkSynced = await SyncGeneratedPlexArtworkAsync(plex, playlistId, request, cancellationToken);
        return BuildGeneratedTargetResult(PlexService, playlistId, "Plex synced generated playlist.", matchSummary, artworkSynced);
    }

    private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToJellyfinAsync(
        GeneratedLocalPlaylistSyncRequest request,
        IReadOnlyList<SyncTrackSummary> tracks,
        List<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError is not null || jellyfin is null)
        {
            return new GeneratedLocalPlaylistTargetResult(JellyfinService, false, configurationError?.Message ?? JellyfinNotConfiguredMessage);
        }

        var jellyfinMatches = await ResolveJellyfinItemIdsAsync(jellyfin, tracks, orderedTrackIds, cancellationToken);
        var itemIds = jellyfinMatches.Select(static item => item.TargetItemId).ToList();
        var matchSummary = BuildGeneratedMatchSummary(tracks, orderedTrackIds, itemIds, jellyfinMatches);
        if (itemIds.Count == 0)
        {
            return BuildGeneratedTargetResult(JellyfinService, null, "Jellyfin skipped: no target tracks resolved.", matchSummary);
        }

        var playlistId = await FindGeneratedJellyfinPlaylistIdAsync(jellyfin, request, cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                request.PlaylistName,
                itemIds,
                cancellationToken);
        }
        else
        {
            var syncItemsResult = await SyncExistingJellyfinPlaylistItemsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                itemIds,
                appendMissingOnly: false,
                cancellationToken);
            if (!syncItemsResult.Success)
            {
                return BuildGeneratedTargetResult(
                    JellyfinService,
                    null,
                    syncItemsResult.ErrorMessage ?? "Jellyfin failed to sync generated playlist items.",
                    matchSummary);
            }
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BuildGeneratedTargetResult(JellyfinService, null, "Jellyfin failed to create generated playlist.", matchSummary);
        }

        await _jellyfinApiClient.UpdateItemMetadataAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            request.PlaylistName,
            request.Description,
            cancellationToken);
        var artworkSynced = await SyncGeneratedJellyfinArtworkAsync(jellyfin, playlistId, request, cancellationToken);
        return BuildGeneratedTargetResult(JellyfinService, playlistId, "Jellyfin synced generated playlist.", matchSummary, artworkSynced);
    }

    private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToNavidromeAsync(
        GeneratedLocalPlaylistSyncRequest request,
        IReadOnlyList<SyncTrackSummary> tracks,
        List<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (configurationError is not null || navidrome is null)
        {
            return new GeneratedLocalPlaylistTargetResult(NavidromeService, false, configurationError?.Message ?? NavidromeNotConfiguredMessage);
        }

        var navidromeMatches = await ResolveNavidromeItemIdsAsync(navidrome, tracks, orderedTrackIds, cancellationToken);
        var itemIds = navidromeMatches.Select(static item => item.TargetItemId).ToList();
        var matchSummary = BuildGeneratedMatchSummary(tracks, orderedTrackIds, itemIds, navidromeMatches);
        if (itemIds.Count == 0)
        {
            return BuildGeneratedTargetResult(NavidromeService, null, "Navidrome skipped: no target tracks resolved.", matchSummary);
        }

        var existingPlaylistId = await FindGeneratedNavidromePlaylistIdAsync(navidrome, request, cancellationToken);
        var playlistId = await _navidromeApiClient.CreateOrUpdatePlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            request.PlaylistName,
            itemIds,
            existingPlaylistId,
            appendMissingOnly: false,
            cancellationToken,
            request.Description);
        var artworkSynced = await SyncGeneratedNavidromeArtworkAsync(navidrome, playlistId, request, cancellationToken);
        return BuildGeneratedTargetResult(
            NavidromeService,
            playlistId,
            string.IsNullOrWhiteSpace(playlistId)
                ? "Navidrome failed to create or update generated playlist."
                : "Navidrome synced generated playlist.",
            matchSummary,
            artworkSynced);
    }

    private async Task<string?> FindGeneratedJellyfinPlaylistIdAsync(
        JellyfinConnection jellyfin,
        GeneratedLocalPlaylistSyncRequest request,
        CancellationToken cancellationToken)
    {
        var stableTitlePrefix = request.StableTitlePrefix.Trim();
        if (!string.IsNullOrWhiteSpace(stableTitlePrefix))
        {
            var existing = (await _jellyfinApiClient.GetPlaylistsAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    jellyfin.UserId,
                    cancellationToken))
                .FirstOrDefault(playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Name)
                    && playlist.Name.StartsWith(stableTitlePrefix, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(existing?.Id))
            {
                return existing.Id;
            }
        }

        return await _jellyfinApiClient.FindPlaylistIdByNameAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            request.PlaylistName,
            cancellationToken);
    }

    private async Task<string?> FindGeneratedNavidromePlaylistIdAsync(
        NavidromeConnection navidrome,
        GeneratedLocalPlaylistSyncRequest request,
        CancellationToken cancellationToken)
    {
        var stableTitlePrefix = request.StableTitlePrefix.Trim();
        if (!string.IsNullOrWhiteSpace(stableTitlePrefix))
        {
            var existing = (await _navidromeApiClient.GetPlaylistsAsync(
                    navidrome.Url,
                    navidrome.Username,
                    navidrome.Password,
                    cancellationToken))
                .FirstOrDefault(playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Name)
                    && playlist.Name.StartsWith(stableTitlePrefix, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(existing?.Id))
            {
                return existing.Id;
            }
        }

        return await _navidromeApiClient.FindPlaylistIdByNameAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            request.PlaylistName,
            cancellationToken);
    }

    private async Task<bool> SyncGeneratedPlexArtworkAsync(
        PlexConnection plex,
        string playlistId,
        GeneratedLocalPlaylistSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ArtworkFilePath) && File.Exists(request.ArtworkFilePath))
        {
            return await _plexApiClient.UpdatePlaylistPosterFromFileAsync(
                plex.Url,
                plex.Token,
                playlistId,
                request.ArtworkFilePath,
                request.ArtworkContentType,
                cancellationToken);
        }

        if (IsAbsoluteHttpUrl(request.ArtworkUrl))
        {
            return await _plexApiClient.UpdatePlaylistPosterFromUrlAsync(
                plex.Url,
                plex.Token,
                playlistId,
                request.ArtworkUrl!,
                cancellationToken);
        }

        return true;
    }

    private async Task<bool> SyncGeneratedJellyfinArtworkAsync(
        JellyfinConnection jellyfin,
        string playlistId,
        GeneratedLocalPlaylistSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ArtworkFilePath) && File.Exists(request.ArtworkFilePath))
        {
            return await _jellyfinApiClient.UpdateItemPrimaryImageFromFileAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                request.ArtworkFilePath,
                request.ArtworkContentType,
                cancellationToken);
        }

        if (IsAbsoluteHttpUrl(request.ArtworkUrl))
        {
            return await _jellyfinApiClient.UpdateItemPrimaryImageFromUrlAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                request.ArtworkUrl!,
                cancellationToken);
        }

        return true;
    }

    private async Task<bool> SyncGeneratedNavidromeArtworkAsync(
        NavidromeConnection navidrome,
        string? playlistId,
        GeneratedLocalPlaylistSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        var artworkPath = !string.IsNullOrWhiteSpace(request.AnimatedArtworkFilePath)
            && File.Exists(request.AnimatedArtworkFilePath)
                ? request.AnimatedArtworkFilePath
                : request.ArtworkFilePath;
        var contentType = string.Equals(artworkPath, request.AnimatedArtworkFilePath, StringComparison.Ordinal)
            ? request.AnimatedArtworkContentType
            : request.ArtworkContentType;
        if (!string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath))
        {
            return await _navidromeApiClient.UpdatePlaylistImageFromFileAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                playlistId,
                artworkPath,
                contentType,
                cancellationToken);
        }

        return true;
    }

    private static SyncMatchSummary BuildGeneratedMatchSummary(
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        List<string> targetIds,
        List<PlaylistWatchTargetMembership> memberships)
        => new(
            targetIds,
            memberships,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: targetIds.Count,
            MissingTracks: Math.Max(0, tracks.Count - targetIds.Count),
            MetadataMatches: 0,
            SearchMatches: targetIds.Count);

    private static GeneratedLocalPlaylistTargetResult BuildGeneratedTargetResult(
        string service,
        string? playlistId,
        string baseMessage,
        SyncMatchSummary matchSummary,
        bool artworkSynced = true)
    {
        var success = !string.IsNullOrWhiteSpace(playlistId) && artworkSynced;
        var message = artworkSynced
            ? baseMessage
            : $"{baseMessage} Playlist artwork did not update.";
        return new GeneratedLocalPlaylistTargetResult(
            service,
            success,
            BuildSyncMessage(message, matchSummary),
            playlistId,
            matchSummary.SourceTracks,
            matchSummary.LocalMatches,
            matchSummary.TargetMatches,
            matchSummary.MissingTracks);
    }

    private static string FormatGeneratedServiceLabel(string service)
        => service switch
        {
            PlexService => "Plex",
            JellyfinService => "Jellyfin",
            NavidromeService => "Navidrome",
            _ => string.IsNullOrWhiteSpace(service) ? "Target" : service
        };

    public sealed record PlaylistMergeSourceInput(
        PlaylistWatchlistDto Playlist,
        PlaylistWatchPreferenceDto? Preference,
        IReadOnlyList<PlaylistTrackCandidate> TrackCandidates);

    public sealed record PlaylistMergeSyncRequest(
        string? PlaylistName,
        string? Description,
        string? ArtworkDataUrl,
        string? ArtworkSource,
        string? ArtworkSourceId,
        string? SourceUsername,
        string? SyncMode,
        bool SyncToPlex,
        bool SyncToJellyfin,
        bool SyncToNavidrome,
        string? ExistingPlexPlaylistId = null,
        string? ExistingJellyfinPlaylistId = null,
        string? ExistingNavidromePlaylistId = null);

    public sealed record PlaylistMergeTargetResult(
        string Target,
        bool Success,
        string Message,
        string? PlaylistId,
        int SyncedTracks);

    public sealed record PlaylistMergeSyncResult(
        bool Success,
        string Message,
        int SourcePlaylists,
        int CandidateTracks,
        int MergedTracks,
        IReadOnlyList<PlaylistMergeTargetResult> Targets);

    public sealed record PlaylistTrackSyncReadiness(
        bool Ready,
        bool Terminal,
        string Message,
        string? Service = null,
        long? LocalTrackId = null,
        string? TargetId = null);

    public sealed record PlaylistTrackAvailability(
        string SourceTrackId,
        bool Eligible,
        bool InLocalLibrary,
        bool InTargetServer,
        long? LocalTrackId = null,
        string? TargetId = null);

    public sealed record PlaylistAvailabilitySummary(
        string? Service,
        int SourceTrackCount,
        int EligibleTrackCount,
        int LocalTrackCount,
        int TargetVisibleTrackCount,
        IReadOnlyList<PlaylistTrackAvailability> Tracks,
        string? ErrorMessage = null);

    public sealed record TargetPlaylistOption(
        string Id,
        string Name,
        int? TrackCount = null);

    public async Task<PlaylistMergeSyncResult> MergeAndSyncPlaylistsAsync(
        IReadOnlyList<PlaylistMergeSourceInput> mergeSources,
        PlaylistMergeSyncRequest request,
        CancellationToken cancellationToken)
    {
        var selectedSources = BuildValidMergeSourceList(mergeSources);
        var validationFailure = ValidateMergeRequest(request, selectedSources.Count);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var (candidateTrackCount, mergedTracks) = await BuildMergedTracksAsync(selectedSources, cancellationToken);

        if (mergedTracks.Count == 0)
        {
            return new PlaylistMergeSyncResult(
                false,
                "No eligible tracks remained after blocked/ignored filtering.",
                selectedSources.Count,
                candidateTrackCount,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        var now = DateTimeOffset.UtcNow;
        var mergedSourceId = Guid.NewGuid().ToString("N");
        var selectedArtworkUrl = await ResolveMergedPlaylistArtworkUrlAsync(
            mergedSourceId,
            request,
            selectedSources,
            cancellationToken);
        var mergedPlaylist = new PlaylistWatchlistDto(
            Id: 0,
            Source: "merged",
            SourceId: mergedSourceId,
            Name: ResolveMergedPlaylistName(request.PlaylistName),
            ImageUrl: selectedArtworkUrl,
            Description: BuildMergedPlaylistDescription(
                request.Description,
                selectedSources.Select(source => source.Playlist),
                request.SourceUsername),
            TrackCount: mergedTracks.Count,
            CreatedAt: now);

        var syncMode = NormalizeSyncMode(request.SyncMode);
        var targets = await SyncMergedPlaylistTargetsAsync(
            request,
            mergedPlaylist,
            mergedTracks,
            syncMode,
            now,
            cancellationToken);

        var anySucceeded = targets.Any(static target => target.Success);
        var allSucceeded = targets.Count > 0 && targets.All(static target => target.Success);
        string message;
        if (allSucceeded)
        {
            message = "Merged playlist synced successfully.";
        }
        else if (anySucceeded)
        {
            message = "Merged playlist synced to some targets. Review target results.";
        }
        else
        {
            message = "Merged playlist sync failed on all selected targets.";
        }
        return new PlaylistMergeSyncResult(
            anySucceeded,
            message,
            selectedSources.Count,
            candidateTrackCount,
            mergedTracks.Count,
            targets);
    }

    private async Task<string?> ResolveMergedPlaylistArtworkUrlAsync(
        string mergedSourceId,
        PlaylistMergeSyncRequest request,
        IReadOnlyList<PlaylistMergeSourceInput> selectedSources,
        CancellationToken cancellationToken)
    {
        var uploadedArtwork = TryParseUploadedArtwork(request.ArtworkDataUrl);
        if (uploadedArtwork is not null)
        {
            return await _playlistVisualService.StoreUploadedVisualAsync(
                "merged",
                mergedSourceId,
                uploadedArtwork.Value.Bytes,
                uploadedArtwork.Value.ContentType,
                cancellationToken);
        }

        var selectedStoredArtwork = ResolveSelectedSourceArtwork(request, selectedSources);
        if (selectedStoredArtwork is not null)
        {
            var bytes = await File.ReadAllBytesAsync(selectedStoredArtwork.FilePath, cancellationToken);
            return await _playlistVisualService.StoreUploadedVisualAsync(
                "merged",
                mergedSourceId,
                bytes,
                selectedStoredArtwork.ContentType,
                cancellationToken);
        }

        return null;
    }

    private PlaylistVisualService.StoredPlaylistVisual? ResolveSelectedSourceArtwork(
        PlaylistMergeSyncRequest request,
        IReadOnlyList<PlaylistMergeSourceInput> selectedSources)
    {
        if (string.IsNullOrWhiteSpace(request.ArtworkSource) || string.IsNullOrWhiteSpace(request.ArtworkSourceId))
        {
            return null;
        }

        var source = request.ArtworkSource.Trim();
        var sourceId = request.ArtworkSourceId.Trim();
        var isSelectedPlaylist = selectedSources.Any(candidate =>
            string.Equals(candidate.Playlist.Source, source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Playlist.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (!isSelectedPlaylist)
        {
            return null;
        }

        var visual = _playlistVisualService.GetStoredVisual(source, sourceId);
        return visual is not null && File.Exists(visual.FilePath) ? visual : null;
    }

    private static UploadedMergeArtwork? TryParseUploadedArtwork(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var trimmed = dataUrl.Trim();
        const string prefix = "data:";
        var commaIndex = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || commaIndex <= prefix.Length)
        {
            return null;
        }

        var metadata = trimmed[prefix.Length..commaIndex];
        var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var contentType = metadataParts.FirstOrDefault(static part => part.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        if (!IsAllowedMergeArtworkContentType(contentType)
            || !metadataParts.Any(static part => part.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(trimmed[(commaIndex + 1)..]);
            return bytes.Length is > 0 and <= MaxUploadedMergeArtworkBytes
                ? new UploadedMergeArtwork(bytes, contentType!)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsAllowedMergeArtworkContentType(string? contentType)
    {
        return contentType is not null
            && (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct UploadedMergeArtwork(byte[] Bytes, string ContentType);

    public async Task<IReadOnlyList<TargetPlaylistOption>> GetTargetPlaylistsAsync(
        string target,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target, PlexService, StringComparison.OrdinalIgnoreCase))
        {
            var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
            if (configurationError is not null || plex is null)
            {
                return Array.Empty<TargetPlaylistOption>();
            }

            var playlists = await _plexApiClient.GetPlaylistsAsync(plex.Url, plex.Token, cancellationToken);
            return playlists
                .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Title))
                .Select(static playlist => new TargetPlaylistOption(
                    playlist.Id!,
                    playlist.Title!,
                    playlist.TrackCount))
                .OrderBy(static playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.Equals(target, JellyfinService, StringComparison.OrdinalIgnoreCase))
        {
            var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
            if (configurationError is not null || jellyfin is null)
            {
                return Array.Empty<TargetPlaylistOption>();
            }

            var playlists = await _jellyfinApiClient.GetPlaylistsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                cancellationToken);
            return playlists
                .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Name))
                .Select(static playlist => new TargetPlaylistOption(
                    playlist.Id!,
                    playlist.Name!,
                    null))
                .OrderBy(static playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.Equals(target, NavidromeService, StringComparison.OrdinalIgnoreCase))
        {
            var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
            if (configurationError is not null || navidrome is null)
            {
                return Array.Empty<TargetPlaylistOption>();
            }

            var playlists = await _navidromeApiClient.GetPlaylistsAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                cancellationToken);
            return playlists
                .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id)
                    && !string.IsNullOrWhiteSpace(playlist.Name))
                .Select(static playlist => new TargetPlaylistOption(
                    playlist.Id,
                    playlist.Name,
                    playlist.TrackCount))
                .OrderBy(static playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return Array.Empty<TargetPlaylistOption>();
    }

    private static List<PlaylistMergeSourceInput> BuildValidMergeSourceList(IReadOnlyList<PlaylistMergeSourceInput> mergeSources)
    {
        return (mergeSources ?? Array.Empty<PlaylistMergeSourceInput>())
            .Where(source => source?.Playlist is not null
                && !string.IsNullOrWhiteSpace(source.Playlist.Source)
                && !string.IsNullOrWhiteSpace(source.Playlist.SourceId))
            .ToList();
    }

    private static PlaylistMergeSyncResult? ValidateMergeRequest(
        PlaylistMergeSyncRequest? request,
        int selectedSourceCount)
    {
        if (request == null)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Merge request is required.",
                0,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        if (selectedSourceCount < 2)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Select at least two monitored playlists to merge.",
                selectedSourceCount,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        if (!request.SyncToPlex && !request.SyncToJellyfin && !request.SyncToNavidrome)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Select at least one destination server (Plex, Jellyfin, or Navidrome).",
                selectedSourceCount,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        return null;
    }

    private async Task<(int CandidateTrackCount, List<SyncTrackSummary> MergedTracks)> BuildMergedTracksAsync(
        IReadOnlyList<PlaylistMergeSourceInput> selectedSources,
        CancellationToken cancellationToken)
    {
        var candidateTrackCount = 0;
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedTracks = new List<SyncTrackSummary>();
        foreach (var source in selectedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = (source.TrackCandidates ?? Array.Empty<PlaylistTrackCandidate>())
                .Select(ToSyncTrackSummary)
                .ToList();
            candidateTrackCount += candidates.Count;

            var filteredTracks = await FilterTracksForSyncAsync(
                source.Playlist,
                source.Preference,
                candidates,
                cancellationToken);

            foreach (var track in filteredTracks)
            {
                var dedupeKey = BuildMergeTrackDedupKey(track);
                if (dedupe.Add(dedupeKey))
                {
                    mergedTracks.Add(track);
                }
            }
        }

        return (candidateTrackCount, mergedTracks);
    }

    private async Task<List<PlaylistMergeTargetResult>> SyncMergedPlaylistTargetsAsync(
        PlaylistMergeSyncRequest request,
        PlaylistWatchlistDto mergedPlaylist,
        IReadOnlyList<SyncTrackSummary> mergedTracks,
        string syncMode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var targets = new List<PlaylistMergeTargetResult>();
        if (request.SyncToPlex)
        {
            var result = await SyncToPlexAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, PlexService, syncMode, now),
                mergedTracks,
                request.ExistingPlexPlaylistId,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                PlexService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        if (request.SyncToJellyfin)
        {
            var result = await SyncToJellyfinAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, JellyfinService, syncMode, now),
                mergedTracks,
                request.ExistingJellyfinPlaylistId,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                JellyfinService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        if (request.SyncToNavidrome)
        {
            var result = await SyncToNavidromeAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, NavidromeService, syncMode, now),
                mergedTracks,
                request.ExistingNavidromePlaylistId,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                NavidromeService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        return targets;
    }

    private static PlaylistWatchPreferenceDto CreateMergedPlaylistPreference(
        PlaylistWatchlistDto mergedPlaylist,
        string service,
        string syncMode,
        DateTimeOffset now)
    {
        return new PlaylistWatchPreferenceDto(
            Source: "merged",
            SourceId: mergedPlaylist.SourceId,
            DestinationFolderId: null,
            Service: service,
            SyncTargets: [service],
            PreferredEngine: null,
            DownloadEngineOrder: null,
            DownloadVariantMode: null,
            SyncMode: syncMode,
            UpdateArtwork: true,
            ReuseSavedArtwork: false,
            CreatedAt: now,
            UpdatedAt: now);
    }

    public async Task<PlaylistSyncResult> SyncPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        bool force,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return PlaylistSyncResult.Failed(PlaylistNotAvailableMessage, PlaylistSyncResultKind.Blocked);
        }

        var services = await ResolveTargetServicesAsync(preference, cancellationToken);
        if (services.Count == 0)
        {
            return PlaylistSyncResult.Failed(NoTargetServerSelectedMessage, PlaylistSyncResultKind.Blocked);
        }

        if (force)
        {
            foreach (var service in services)
            {
                await _mediaServerRefreshService.RefreshAsync(service, cancellationToken);
            }
        }

        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return PlaylistSyncResult.FailedFromMessage(loadResult.ErrorMessage);
        }

        var tracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            loadResult.Tracks,
            cancellationToken);
        if (tracks.Count == 0)
        {
            return PlaylistSyncResult.Failed("No eligible tracks after blocked/ignored filtering.", PlaylistSyncResultKind.Blocked);
        }

        var result = await SyncPlaylistToTargetsAsync(
            services,
            playlist,
            preference,
            tracks,
            cancellationToken);

        await PublishWatchlistSyncUpdatedAsync(playlist, result, cancellationToken);
        return result;
    }

    public async Task<PlaylistSyncResult> SyncAvailablePlaylistTracksAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        bool force,
        CancellationToken cancellationToken)
        => await SyncAvailablePlaylistTracksAsync(
            playlist,
            preference,
            trackCandidates,
            targetService: null,
            force,
            cancellationToken);

    public async Task<PlaylistSyncResult> SyncAvailablePlaylistTracksAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        string? targetService,
        bool force,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return PlaylistSyncResult.Failed(PlaylistNotAvailableMessage, PlaylistSyncResultKind.Blocked);
        }

        var services = await ResolveTargetServicesAsync(preference, targetService, cancellationToken);
        if (services.Count == 0)
        {
            return PlaylistSyncResult.Failed(
                string.IsNullOrWhiteSpace(targetService)
                    ? NoTargetServerSelectedMessage
                    : UnsupportedPlaylistSyncTargetMessage,
                PlaylistSyncResultKind.Blocked);
        }

        if (force)
        {
            foreach (var service in services)
            {
                await _mediaServerRefreshService.RefreshAsync(service, cancellationToken);
            }
        }

        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return PlaylistSyncResult.FailedFromMessage(loadResult.ErrorMessage);
        }

        var eligibleTracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            loadResult.Tracks,
            cancellationToken);
        if (eligibleTracks.Count == 0)
        {
            return PlaylistSyncResult.Failed("No eligible tracks after blocked/ignored filtering.", PlaylistSyncResultKind.Blocked);
        }

        var availableTrackRows = await ResolvePersistedAvailableTrackRowsAsync(
            playlist.Source,
            playlist.SourceId,
            eligibleTracks,
            cancellationToken);

        if (availableTrackRows.Count == 0)
        {
            await EnsureTargetPlaylistContainersForServicesAsync(
                playlist,
                preference,
                services,
                cancellationToken);
            return PlaylistSyncResult.NoLocalTracks(
                "No eligible playlist tracks are visible in the DeezSpoTag library yet.",
                sourceTracks: eligibleTracks.Count,
                missingTracks: eligibleTracks.Count);
        }

        var availableTracks = availableTrackRows.Select(static row => row.Track).ToList();

        var result = await SyncPlaylistToTargetsAsync(
            services,
            playlist,
            preference,
            availableTracks,
            cancellationToken);

        if (!result.Success)
        {
            return result;
        }

        var unavailableCount = Math.Max(0, eligibleTracks.Count - availableTracks.Count);
        if (unavailableCount == 0)
        {
            await PublishWatchlistSyncUpdatedAsync(playlist, result, cancellationToken);
            return result;
        }

        // Membership of currently available tracks is complete. Remaining eligible
        // tracks stay deferred for download; all targets share this success policy.
        var deferredResult = result with
        {
            Success = true,
            Message = string.Concat(
                result.Message,
                " ",
                unavailableCount.ToString(CultureInfo.InvariantCulture),
                " eligible track(s) are still missing and were left for download/retry."),
            SourceTracks = eligibleTracks.Count,
            MissingTracks = unavailableCount + result.MissingTracks
        };
        await PublishWatchlistSyncUpdatedAsync(playlist, deferredResult, cancellationToken);
        return deferredResult;
    }

    private async Task<PlaylistSyncResult> SyncPlaylistToTargetsAsync(
        IReadOnlyList<string> services,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var results = new List<(string Service, PlaylistSyncResult Result)>(services.Count);
        foreach (var service in services)
        {
            var result = await SyncPlaylistToTargetAsync(service, playlist, preference, tracks, cancellationToken);
            results.Add((service, result));
        }

        return CombinePlaylistSyncTargetResults(results);
    }

    /// <summary>
    /// Creates (or verifies) the destination playlist container -- name, description, artwork --
    /// on every one of the playlist's configured sync targets, independent of whether any track
    /// has downloaded yet. Intended to be called synchronously right after a user saves monitored-
    /// playlist settings, so the playlist appears on the target server(s) immediately instead of
    /// only after the first batch of tracks finishes downloading and the normal membership-sync
    /// pass happens to run (which previously would not create anything at all until at least one
    /// track was locally available -- see SyncToPlexAsync/SyncToJellyfinAsync/SyncToNavidromeAsync's
    /// "zero matched tracks" gate).
    ///
    /// Jellyfin and Navidrome playlists can be created with zero items, so this is genuinely
    /// instant for those two. Plex's classic playlist-creation endpoint requires a seed item, so a
    /// truly empty Plex playlist can't be created here -- that case is reported as deferred rather
    /// than attempted, and Plex creation still happens via the normal membership-sync pass as soon
    /// as the first track is available.
    /// </summary>
    public async Task<IReadOnlyList<PlaylistProvisioningOutcome>> EnsureTargetPlaylistContainersAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var services = await ResolveTargetServicesAsync(preference, cancellationToken);
        if (services.Count == 0)
        {
            return [];
        }

        return await EnsureTargetPlaylistContainersForServicesAsync(
            playlist,
            preference,
            services,
            cancellationToken);
    }

    private async Task<IReadOnlyList<PlaylistProvisioningOutcome>> EnsureTargetPlaylistContainersForServicesAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<string> services,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<PlaylistProvisioningOutcome>(services.Count);
        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(service switch
            {
                JellyfinService => await EnsureJellyfinPlaylistContainerAsync(playlist, preference, cancellationToken),
                NavidromeService => await EnsureNavidromePlaylistContainerAsync(playlist, preference, cancellationToken),
                PlexService => new PlaylistProvisioningOutcome(
                    PlexService,
                    Created: false,
                    PlaylistId: ResolveExistingTargetPlaylistId(preference, PlexService),
                    Message: "Plex requires at least one track to create a playlist; it will be created automatically once the first track downloads."),
                _ => new PlaylistProvisioningOutcome(service, false, null, "Unsupported playlist sync target.")
            });
        }

        return outcomes;
    }

    private async Task<PlaylistProvisioningOutcome> EnsureJellyfinPlaylistContainerAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (jellyfin == null)
        {
            return new PlaylistProvisioningOutcome(JellyfinService, false, null, configurationError?.Message ?? JellyfinNotConfiguredMessage);
        }

        var storedPlaylistId = ResolveExistingTargetPlaylistId(preference, JellyfinService);
        var playlistLookup = await ResolveAuthoritativeJellyfinPlaylistIdAsync(jellyfin, playlist, storedPlaylistId, cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return new PlaylistProvisioningOutcome(
                JellyfinService,
                false,
                storedPlaylistId,
                "Jellyfin playlist lookup timed out.");
        }

        var playlistId = playlistLookup.Status == TargetLookupStatus.Success ? playlistLookup.Value : null;
        var created = false;
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                ResolvePlaylistName(playlist),
                itemIds: [],
                cancellationToken);
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                return new PlaylistProvisioningOutcome(JellyfinService, false, null, "Failed to create Jellyfin playlist.");
            }

            created = true;
        }

        await PersistTargetPlaylistBindingAsync(playlist, preference, JellyfinService, playlistId, cancellationToken);
        var metadataSynced = await SyncJellyfinPlaylistMetadataAsync(jellyfin, playlist, playlistId, cancellationToken);
        var artworkSynced = await SyncJellyfinPlaylistArtworkAsync(jellyfin, playlist, preference, playlistId, cancellationToken);
        return new PlaylistProvisioningOutcome(
            JellyfinService,
            created,
            playlistId,
            BuildProvisioningMessage("Jellyfin", created, metadataSynced, artworkSynced));
    }

    private async Task<PlaylistProvisioningOutcome> EnsureNavidromePlaylistContainerAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (navidrome == null)
        {
            return new PlaylistProvisioningOutcome(NavidromeService, false, null, configurationError?.Message ?? NavidromeNotConfiguredMessage);
        }

        var storedPlaylistId = ResolveExistingTargetPlaylistId(preference, NavidromeService);
        var playlistLookup = await ResolveAuthoritativeNavidromePlaylistIdAsync(navidrome, playlist, storedPlaylistId, cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return new PlaylistProvisioningOutcome(
                NavidromeService,
                false,
                storedPlaylistId,
                "Navidrome playlist lookup timed out.");
        }

        var playlistId = playlistLookup.Status == TargetLookupStatus.Success ? playlistLookup.Value : null;
        var created = false;
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await _navidromeApiClient.CreateOrUpdatePlaylistAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                ResolvePlaylistName(playlist),
                songIds: [],
                existingPlaylistId: null,
                appendMissingOnly: false,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                return new PlaylistProvisioningOutcome(NavidromeService, false, null, "Failed to create Navidrome playlist.");
            }

            created = true;
        }

        await PersistTargetPlaylistBindingAsync(playlist, preference, NavidromeService, playlistId, cancellationToken);
        var metadataSynced = await SyncNavidromePlaylistMetadataAsync(navidrome, playlist, playlistId, cancellationToken);
        var artworkSynced = await SyncNavidromePlaylistArtworkAsync(navidrome, playlist, preference, playlistId, cancellationToken);
        return new PlaylistProvisioningOutcome(
            NavidromeService,
            created,
            playlistId,
            BuildProvisioningMessage("Navidrome", created, metadataSynced, artworkSynced));
    }

    internal static string BuildProvisioningMessage(string targetLabel, bool created, bool metadataSynced, bool artworkSynced)
    {
        var message = created ? $"{targetLabel} playlist created." : $"{targetLabel} playlist already exists.";
        if (!metadataSynced)
        {
            message = string.Concat(message, " Name/description did not verify.");
        }

        if (!artworkSynced)
        {
            message = string.Concat(message, " Artwork was not applied (no cached cover yet, or the update failed).");
        }

        return message;
    }

    private async Task<PlaylistSyncResult> SyncPlaylistToTargetAsync(
        string service,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        try
        {
            return service switch
            {
                PlexService => await SyncToPlexAsync(playlist, preference, tracks, ResolveExistingTargetPlaylistId(preference, PlexService), cancellationToken),
                JellyfinService => await SyncToJellyfinAsync(playlist, preference, tracks, ResolveExistingTargetPlaylistId(preference, JellyfinService), cancellationToken),
                NavidromeService => await SyncToNavidromeAsync(playlist, preference, tracks, ResolveExistingTargetPlaylistId(preference, NavidromeService), cancellationToken),
                _ => PlaylistSyncResult.Failed(UnsupportedPlaylistSyncTargetMessage, PlaylistSyncResultKind.Blocked)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Playlist sync target {Target} failed for {Source}:{SourceId}; continuing with remaining enabled targets.",
                FormatTargetServiceLabel(service),
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId));
            return PlaylistSyncResult.FailedFromMessage($"{FormatTargetServiceLabel(service)} sync failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> ResolveTargetServicesAsync(
        PlaylistWatchPreferenceDto? preference,
        string? targetService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetService))
        {
            return await ResolveTargetServicesAsync(preference, cancellationToken);
        }

        var normalizedTarget = NormalizeService(targetService);
        if (normalizedTarget is not (PlexService or JellyfinService or NavidromeService))
        {
            return Array.Empty<string>();
        }

        var configuredTargets = await ResolveTargetServicesAsync(preference, cancellationToken);
        return configuredTargets.Contains(normalizedTarget, StringComparer.OrdinalIgnoreCase)
            ? new[] { normalizedTarget }
            : Array.Empty<string>();
    }

    internal static PlaylistSyncResult CombinePlaylistSyncTargetResults(
        IReadOnlyList<(string Service, PlaylistSyncResult Result)> results)
    {
        if (results.Count == 0)
        {
            return PlaylistSyncResult.Failed(NoTargetServerSelectedMessage, PlaylistSyncResultKind.Blocked);
        }

        if (results.Count == 1)
        {
            return results[0].Result;
        }

        var successfulResults = results.Where(item => item.Result.Success).ToList();
        var message = string.Join(
            " ",
            results.Select(item => string.Concat(
                FormatTargetServiceLabel(item.Service),
                ": ",
                item.Result.Message)));

        if (successfulResults.Count != results.Count)
        {
            var first = results.First(item => !item.Result.Success).Result;
            return first with
            {
                Success = false,
                Message = message,
                SyncedTracks = successfulResults.Sum(item => item.Result.SyncedTracks),
                TargetMatches = successfulResults.Sum(item => item.Result.TargetMatches),
                MetadataMatches = successfulResults.Sum(item => item.Result.MetadataMatches),
                SearchMatches = successfulResults.Sum(item => item.Result.SearchMatches),
                Kind = first.Kind
            };
        }

        var aggregate = successfulResults[0].Result;
        var combinedKind = successfulResults.Any(item => item.Result.Kind == PlaylistSyncResultKind.IdentityGap)
            ? PlaylistSyncResultKind.IdentityGap
            : aggregate.Kind;
        return aggregate with
        {
            Success = true,
            Message = message,
            SyncedTracks = successfulResults.Sum(item => item.Result.SyncedTracks),
            TargetMatches = successfulResults.Sum(item => item.Result.TargetMatches),
            MetadataMatches = successfulResults.Sum(item => item.Result.MetadataMatches),
            SearchMatches = successfulResults.Sum(item => item.Result.SearchMatches),
            Kind = combinedKind
        };
    }

    private static string FormatTargetServiceLabel(string service)
        => NormalizeService(service) switch
        {
            PlexService => "Plex",
            JellyfinService => "Jellyfin",
            NavidromeService => "Navidrome",
            _ => service
        };

    private async Task PublishWatchlistSyncUpdatedAsync(
        PlaylistWatchlistDto playlist,
        PlaylistSyncResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Success || _crossDeviceSyncService is null)
        {
            return;
        }

        await _crossDeviceSyncService.PublishWatchlistUpdatedAsync(
            playlist.Source,
            playlist.SourceId,
            "playlist_sync_completed",
            cancellationToken);
    }

    public async Task<PlaylistAvailabilitySummary> GetPlaylistAvailabilityAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return new PlaylistAvailabilitySummary(null, 0, 0, 0, 0, Array.Empty<PlaylistTrackAvailability>(), PlaylistNotAvailableMessage);
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return new PlaylistAvailabilitySummary(service, 0, 0, 0, 0, Array.Empty<PlaylistTrackAvailability>(), loadResult.ErrorMessage);
        }

        var allTracks = loadResult.Tracks
            .Where(static track => !string.IsNullOrWhiteSpace(track.SourceTrackId))
            .ToList();
        var eligibleTracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            allTracks,
            cancellationToken);
        var eligibleIds = eligibleTracks
            .Select(static track => track.SourceTrackId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableTrackRows = await ResolveAvailableTrackRowsAsync(
            playlist.Source,
            eligibleTracks,
            cancellationToken);
        var localTrackIdBySourceId = availableTrackRows
            .Where(static row => !string.IsNullOrWhiteSpace(row.Track.SourceTrackId))
            .GroupBy(static row => row.Track.SourceTrackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().LocalTrackId, StringComparer.OrdinalIgnoreCase);
        var targetIdBySourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(service) && availableTrackRows.Count > 0)
        {
            var targetIds = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
                service,
                availableTrackRows
                    .Select(static row => row.LocalTrackId)
                    .Where(static id => id > 0)
                    .Distinct()
                    .ToList(),
                cancellationToken);
            foreach (var row in availableTrackRows)
            {
                if (!string.IsNullOrWhiteSpace(row.Track.SourceTrackId)
                    && targetIds.TryGetValue(row.LocalTrackId, out var targetId)
                    && !string.IsNullOrWhiteSpace(targetId))
                {
                    targetIdBySourceId[row.Track.SourceTrackId] = targetId;
                }
            }
        }

        var availability = allTracks
            .Select(track =>
            {
                var localTrackId = localTrackIdBySourceId.TryGetValue(track.SourceTrackId, out var resolvedLocalTrackId)
                    ? resolvedLocalTrackId
                    : (long?)null;
                var targetId = targetIdBySourceId.TryGetValue(track.SourceTrackId, out var resolvedTargetId)
                    ? resolvedTargetId
                    : null;
                return new PlaylistTrackAvailability(
                    track.SourceTrackId,
                    eligibleIds.Contains(track.SourceTrackId),
                    localTrackId.HasValue,
                    !string.IsNullOrWhiteSpace(targetId),
                    localTrackId,
                    targetId);
            })
            .ToList();

        return new PlaylistAvailabilitySummary(
            service,
            allTracks.Count,
            eligibleTracks.Count,
            localTrackIdBySourceId.Count,
            targetIdBySourceId.Count,
            availability);
    }

    private async Task<List<(SyncTrackSummary Track, long LocalTrackId)>> ResolveAvailableTrackRowsAsync(
        string source,
        IReadOnlyList<SyncTrackSummary> eligibleTracks,
        CancellationToken cancellationToken)
    {
        var availableTrackRows = new List<(SyncTrackSummary Track, long LocalTrackId)>(eligibleTracks.Count);
        var localTrackIds = await ResolveLocalTrackIdsAsync(source, eligibleTracks, cancellationToken);
        for (var index = 0; index < eligibleTracks.Count; index++)
        {
            if (localTrackIds[index] > 0)
            {
                availableTrackRows.Add((eligibleTracks[index], localTrackIds[index]));
            }
        }

        return availableTrackRows;
    }

    private async Task<List<(SyncTrackSummary Track, long LocalTrackId)>> ResolvePersistedAvailableTrackRowsAsync(
        string source,
        string sourceId,
        IReadOnlyList<SyncTrackSummary> eligibleTracks,
        CancellationToken cancellationToken)
    {
        var localTrackIdBySourceId = (await _libraryRepository.GetPlaylistWatchTrackStatusesAsync(
                source,
                sourceId,
                cancellationToken))
            .Where(static status => status.LocalTrackId.HasValue
                                    && !string.Equals(status.IdentityStatus, "review", StringComparison.OrdinalIgnoreCase))
            .GroupBy(static status => status.TrackSourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().LocalTrackId!.Value,
                StringComparer.OrdinalIgnoreCase);
        return eligibleTracks
            .Where(track => localTrackIdBySourceId.ContainsKey(track.SourceTrackId))
            .Select(track => (track, localTrackIdBySourceId[track.SourceTrackId]))
            .ToList();
    }

    public async Task<PlaylistSyncResult> SyncPlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return PlaylistSyncResult.Failed(PlaylistNotAvailableMessage, PlaylistSyncResultKind.Blocked);
        }

        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return PlaylistSyncResult.Completed("Playlist artwork sync disabled.");
        }

        var services = await ResolveTargetServicesAsync(preference, cancellationToken);
        if (services.Count == 0)
        {
            return PlaylistSyncResult.Failed(NoTargetServerSelectedMessage, PlaylistSyncResultKind.Blocked);
        }

        var results = new List<(string Service, PlaylistSyncResult Result)>(services.Count);
        foreach (var service in services)
        {
            var result = service switch
            {
                PlexService => await SyncPlexPlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
                JellyfinService => await SyncJellyfinPlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
                NavidromeService => await SyncNavidromePlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
                _ => PlaylistSyncResult.Failed(UnsupportedPlaylistSyncTargetMessage, PlaylistSyncResultKind.Blocked)
            };
            results.Add((service, result));
            var revision = _playlistVisualService.GetTargetArtworkRevision(
                playlist.Source,
                playlist.SourceId,
                service);
            if (!string.IsNullOrWhiteSpace(revision))
            {
                await _libraryRepository.SetPlaylistWatchArtworkTargetStateAsync(
                    playlist.Source,
                    playlist.SourceId,
                    service,
                    revision,
                    result.Success,
                    result.Success ? null : result.Message,
                    cancellationToken);
            }
        }

        return CombinePlaylistSyncTargetResults(results);
    }

    public async Task<PlaylistSyncResult> SyncPlaylistArtworkToTargetAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string targetService,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return PlaylistSyncResult.Completed("Playlist artwork sync disabled.");
        }

        return NormalizeService(targetService) switch
        {
            PlexService => await SyncPlexPlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
            JellyfinService => await SyncJellyfinPlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
            NavidromeService => await SyncNavidromePlaylistArtworkOnlyAsync(playlist, preference, cancellationToken),
            _ => PlaylistSyncResult.Failed(UnsupportedPlaylistSyncTargetMessage, PlaylistSyncResultKind.Blocked)
        };
    }

    public async Task<bool> IsPlaylistArtworkCurrentOnTargetAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string targetService,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return true;
        }

        var normalizedTarget = NormalizeService(targetService);
        var stillVisual = _playlistVisualService.GetActiveStoredStillVisual(playlist.Source, playlist.SourceId);
        if (normalizedTarget == PlexService)
        {
            var (plex, error) = await TryLoadConfiguredPlexAsync();
            if (error != null || plex == null || stillVisual == null)
            {
                return false;
            }

            var playlistLookup = await ResolveAuthoritativePlexPlaylistIdAsync(
                plex, playlist, ResolveExistingTargetPlaylistId(preference, PlexService), cancellationToken);
            return playlistLookup.Status == TargetLookupStatus.Success
                && !string.IsNullOrWhiteSpace(playlistLookup.Value)
                && await _plexApiClient.VerifyPlaylistPosterFromFileAsync(
                    plex.Url, plex.Token, playlistLookup.Value, stillVisual.FilePath, cancellationToken);
        }

        if (normalizedTarget == JellyfinService)
        {
            var (jellyfin, error) = await TryLoadConfiguredJellyfinAsync();
            if (error != null || jellyfin == null || stillVisual == null)
            {
                return false;
            }

            var playlistLookup = await ResolveAuthoritativeJellyfinPlaylistIdAsync(
                jellyfin, playlist, ResolveExistingTargetPlaylistId(preference, JellyfinService), cancellationToken);
            return playlistLookup.Status == TargetLookupStatus.Success
                && !string.IsNullOrWhiteSpace(playlistLookup.Value)
                && await _jellyfinApiClient.VerifyItemPrimaryImageFromFileAsync(
                    jellyfin.Url, jellyfin.ApiKey, playlistLookup.Value, stillVisual.FilePath, cancellationToken);
        }

        if (normalizedTarget == NavidromeService)
        {
            var (navidrome, error) = await TryLoadConfiguredNavidromeAsync();
            if (error != null || navidrome == null)
            {
                return false;
            }

            var visual = await _playlistVisualService.ResolveApplePlaylistAnimatedVisualAsync(
                    playlist.Source, playlist.SourceId, cancellationToken)
                ?? stillVisual;
            if (visual == null)
            {
                return false;
            }

            var playlistLookup = await ResolveAuthoritativeNavidromePlaylistIdAsync(
                navidrome, playlist, ResolveExistingTargetPlaylistId(preference, NavidromeService), cancellationToken);
            return playlistLookup.Status == TargetLookupStatus.Success
                && !string.IsNullOrWhiteSpace(playlistLookup.Value)
                && await _navidromeApiClient.VerifyPlaylistImageFromFileAsync(
                    navidrome.Url,
                    navidrome.Username,
                    navidrome.Password,
                    playlistLookup.Value,
                    visual.FilePath,
                    cancellationToken);
        }

        return false;
    }

    public async Task<PlaylistTrackSyncReadiness> CheckTrackReadyForAutomaticSyncAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        PlaylistTrackCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (playlist == null || candidate == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist or track candidate is unavailable.");
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistTrackSyncReadiness(false, true, NoTargetServerSelectedMessage);
        }

        if (string.Equals(service, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist sync target is disabled.", service);
        }

        var track = ToSyncTrackSummary(candidate);
        var localTrackId = await ResolveLocalTrackIdAsync(playlist.Source, track, cancellationToken);
        if (!localTrackId.HasValue)
        {
            return new PlaylistTrackSyncReadiness(
                false,
                false,
                "Track is not visible in the DeezSpoTag library yet.",
                service);
        }

        return service switch
        {
            PlexService => await CheckPlexTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            JellyfinService => await CheckJellyfinTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            NavidromeService => await CheckNavidromeTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            _ => new PlaylistTrackSyncReadiness(false, true, UnsupportedPlaylistSyncTargetMessage, service, localTrackId)
        };
    }

    private async Task<PlaylistTrackSyncReadiness> CheckTargetTrackReadyAsync(
        PlaylistWatchPreferenceDto? preference,
        SyncTrackSummary track,
        long localTrackId,
        CancellationToken cancellationToken)
    {
        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistTrackSyncReadiness(false, true, NoTargetServerSelectedMessage);
        }

        if (string.Equals(service, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist sync target is disabled.", service, localTrackId);
        }

        return service switch
        {
            PlexService => await CheckPlexTrackReadyAsync(localTrackId, track, cancellationToken),
            JellyfinService => await CheckJellyfinTrackReadyAsync(localTrackId, track, cancellationToken),
            NavidromeService => await CheckNavidromeTrackReadyAsync(localTrackId, track, cancellationToken),
            _ => new PlaylistTrackSyncReadiness(false, true, UnsupportedPlaylistSyncTargetMessage, service, localTrackId)
        };
    }

    private async Task<(IReadOnlyList<SyncTrackSummary> Tracks, string? ErrorMessage)> LoadTracksForSyncAsync(
        PlaylistWatchlistDto playlist,
        IReadOnlyList<PlaylistTrackCandidate>? trackCandidates,
        CancellationToken cancellationToken)
    {
        var source = NormalizeSource(playlist.Source);
        if (trackCandidates is { Count: > 0 })
        {
            var candidates = PlaylistCandidateContract.ResolvableCandidates(source, trackCandidates);
            return (candidates.Select(ToSyncTrackSummary).ToList(), null);
        }

        if (string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _spotifyMetadataService.FetchPlaylistSnapshotAsync(playlist.SourceId, cancellationToken);
            if (snapshot != null && snapshot.Tracks.Count > 0)
            {
                return (snapshot.Tracks.Select(ToSyncTrackSummary).ToList(), null);
            }

            return (Array.Empty<SyncTrackSummary>(), "Spotify playlist could not be loaded.");
        }

        return (Array.Empty<SyncTrackSummary>(), "Track candidates are unavailable for this source. Open playlist settings once and retry sync.");
    }

    private async Task<PlaylistSyncResult> SyncToPlexAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (plex == null)
        {
            return PlaylistSyncResult.Failed(PlexNotConfiguredMessage, PlaylistSyncResultKind.Retry);
        }

        var playlistName = ResolvePlaylistName(playlist);
        var storedPlaylistId = NormalizeExistingTargetPlaylistId(existingPlaylistId);
        var playlistLookup = await ResolveAuthoritativePlexPlaylistIdAsync(
            plex,
            playlist,
            existingPlaylistId,
            cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return PlaylistSyncResult.Failed("Plex playlist lookup timed out.", PlaylistSyncResultKind.Retry);
        }

        existingPlaylistId = playlistLookup.Status == TargetLookupStatus.Success ? playlistLookup.Value : null;
        if (!string.IsNullOrWhiteSpace(existingPlaylistId))
        {
            await _plexApiClient.UpdatePlaylistMetadataAsync(
                plex.Url,
                plex.Token,
                existingPlaylistId,
                playlistName,
                playlist.Description,
                cancellationToken);
        }

        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var matchSummary = await ResolvePlexRatingKeysAsync(plex, tracks, orderedTrackIds, cancellationToken);
        if (matchSummary.TargetIds.Count == 0)
        {
            _logger.LogWarning(
                "No Plex matches found for playlist {Source}:{SourceId}. sourceTracks={SourceTracks}, localMatches={LocalMatches}, missingTracks={MissingTracks}",
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId),
                matchSummary.SourceTracks,
                matchSummary.LocalMatches,
                matchSummary.MissingTracks);
            if (!string.IsNullOrWhiteSpace(existingPlaylistId))
            {
                await PersistTargetPlaylistBindingAsync(
                    playlist,
                    preference,
                    PlexService,
                    existingPlaylistId,
                    cancellationToken);
                await _plexApiClient.UpdatePlaylistMetadataAsync(
                    plex.Url,
                    plex.Token,
                    existingPlaylistId,
                    playlistName,
                    playlist.Description,
                    cancellationToken);
            }

            return await CompleteTargetMembershipAsync(
                playlist,
                preference,
                PlexService,
                existingPlaylistId,
                storedPlaylistId,
                matchSummary,
                Array.Empty<PlaylistWatchTargetMembership>(),
                tracks,
                orderedTrackIds,
                writeComplete: true,
                successBaseMessage: "No Plex matches found for this playlist.",
                applyArtwork: () => SyncPlexPlaylistArtworkAsync(
                    plex,
                    playlist,
                    preference,
                    existingPlaylistId ?? string.Empty,
                    cancellationToken),
                extraSuccessSuffix: null,
                cancellationToken);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        var upsert = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            playlistName,
            matchSummary.TargetIds,
            options: new PlexApiClient.PlaylistUpsertOptions(
                AppendMissingOnly: appendMissingOnly,
                ExistingPlaylistId: string.IsNullOrWhiteSpace(existingPlaylistId)
                    ? null
                    : existingPlaylistId.Trim()),
            cancellationToken: cancellationToken);
        var playlistId = upsert.PlaylistId;
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BuildWriteFailureResult(
                BuildSyncMessage("Failed to create or update Plex playlist.", matchSummary),
                matchSummary);
        }

        await PersistTargetPlaylistBindingAsync(
            playlist,
            preference,
            PlexService,
            playlistId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(existingPlaylistId)
            || !string.Equals(existingPlaylistId, playlistId, StringComparison.OrdinalIgnoreCase))
        {
            await _plexApiClient.UpdatePlaylistMetadataAsync(
                plex.Url,
                plex.Token,
                playlistId,
                playlistName,
                playlist.Description,
                cancellationToken);
        }

        var verifiedMemberships = await ReadVerifiedPlexMembershipsAsync(
            plex,
            playlistId,
            matchSummary.Memberships,
            cancellationToken);
        if (!IsResolvedMembershipVerified(matchSummary.TargetMatches, verifiedMemberships.Count, upsert.Complete))
        {
            await InvalidateConfirmedMissingPlexIdentitiesAsync(
                plex,
                matchSummary.Memberships,
                verifiedMemberships,
                cancellationToken);
        }

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        return await CompleteTargetMembershipAsync(
            playlist,
            preference,
            PlexService,
            playlistId,
            storedPlaylistId,
            matchSummary,
            verifiedMemberships,
            tracks,
            orderedTrackIds,
            writeComplete: upsert.Complete,
            successBaseMessage: $"Playlist synced ({modeLabel}).",
            applyArtwork: () => SyncPlexPlaylistArtworkAsync(
                plex,
                playlist,
                preference,
                playlistId,
                cancellationToken),
            extraSuccessSuffix: null,
            cancellationToken);
    }

    private async Task<PlaylistSyncResult> SyncToJellyfinAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (jellyfin == null)
        {
            return PlaylistSyncResult.Failed(JellyfinNotConfiguredMessage, PlaylistSyncResultKind.Retry);
        }

        var playlistName = ResolvePlaylistName(playlist);
        var storedPlaylistId = NormalizeExistingTargetPlaylistId(existingPlaylistId);
        var playlistLookup = await ResolveAuthoritativeJellyfinPlaylistIdAsync(
            jellyfin,
            playlist,
            existingPlaylistId,
            cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return PlaylistSyncResult.Failed("Jellyfin playlist lookup timed out.", PlaylistSyncResultKind.Retry);
        }

        existingPlaylistId = playlistLookup.Status == TargetLookupStatus.Success ? playlistLookup.Value : null;
        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var jellyfinMatches = await ResolveJellyfinItemIdsAsync(jellyfin, tracks, orderedTrackIds, cancellationToken);
        var itemIds = jellyfinMatches.Select(static item => item.TargetItemId).ToList();
        var matchSummary = new SyncMatchSummary(
            itemIds,
            jellyfinMatches,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: itemIds.Count,
            MissingTracks: Math.Max(0, tracks.Count - itemIds.Count),
            MetadataMatches: 0,
            SearchMatches: itemIds.Count);
        if (itemIds.Count == 0)
        {
            _logger.LogWarning(
                "No Jellyfin matches found for playlist {Source}:{SourceId}.",
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId));
            if (!string.IsNullOrWhiteSpace(existingPlaylistId))
            {
                await SyncJellyfinPlaylistMetadataAsync(jellyfin, playlist, existingPlaylistId, cancellationToken);
            }

            var emptyPlaylistId = await EnsureJellyfinPlaylistContainerAsync(
                jellyfin,
                playlist,
                preference,
                playlistName,
                existingPlaylistId,
                cancellationToken);
            return await CompleteTargetMembershipAsync(
                playlist,
                preference,
                JellyfinService,
                emptyPlaylistId,
                storedPlaylistId,
                matchSummary,
                Array.Empty<PlaylistWatchTargetMembership>(),
                tracks,
                orderedTrackIds,
                writeComplete: true,
                successBaseMessage: "No Jellyfin matches found for this playlist.",
                applyArtwork: () => SyncJellyfinPlaylistArtworkAsync(
                    jellyfin,
                    playlist,
                    preference,
                    emptyPlaylistId ?? string.Empty,
                    cancellationToken),
                extraSuccessSuffix: null,
                cancellationToken);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        var playlistId = existingPlaylistId;
        var metadataSynced = true;
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            metadataSynced = await SyncJellyfinPlaylistMetadataAsync(
                jellyfin,
                playlist,
                playlistId,
                cancellationToken);
            var syncItemsResult = await SyncExistingJellyfinPlaylistItemsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                itemIds,
                appendMissingOnly,
                cancellationToken);
            if (!syncItemsResult.Success)
            {
                return BuildWriteFailureResult(
                    BuildSyncMessage(syncItemsResult.ErrorMessage ?? "Failed to sync Jellyfin playlist.", matchSummary),
                    matchSummary);
            }

            if (!appendMissingOnly)
            {
                var reorder = await TryReorderJellyfinPlaylistAsync(
                    jellyfin,
                    playlistId,
                    itemIds,
                    cancellationToken);
                if (reorder.Status == JellyfinPlaylistMoveStatus.Transient)
                {
                    return BuildWriteFailureResult(
                        BuildSyncMessage("Jellyfin playlist reorder timed out.", matchSummary),
                        matchSummary,
                        playlistId);
                }
            }
        }
        else
        {
            var createdPlaylistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistName,
                itemIds,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(createdPlaylistId))
            {
                return BuildWriteFailureResult(
                    BuildSyncMessage("Failed to create Jellyfin playlist.", matchSummary),
                    matchSummary);
            }

            playlistId = createdPlaylistId;
            metadataSynced = await SyncJellyfinPlaylistMetadataAsync(
                jellyfin,
                playlist,
                playlistId,
                cancellationToken);
        }

        await PersistTargetPlaylistBindingAsync(playlist, preference, JellyfinService, playlistId, cancellationToken);
        var verifiedMemberships = await ReadVerifiedJellyfinMembershipsAsync(
            jellyfin,
            playlistId,
            jellyfinMatches,
            cancellationToken);

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        var fullSyncIssues = BuildJellyfinFullSyncIssues(metadataSynced);
        return await CompleteTargetMembershipAsync(
            playlist,
            preference,
            JellyfinService,
            playlistId,
            storedPlaylistId,
            matchSummary with { TargetIds = itemIds },
            verifiedMemberships,
            tracks,
            orderedTrackIds,
            writeComplete: true,
            successBaseMessage: $"Playlist synced ({modeLabel}).",
            applyArtwork: () => SyncJellyfinPlaylistArtworkAsync(
                jellyfin,
                playlist,
                preference,
                playlistId,
                cancellationToken),
            extraSuccessSuffix: fullSyncIssues.Count == 0 ? null : string.Join(" ", fullSyncIssues),
            cancellationToken);
    }

    private async Task<PlaylistSyncResult> SyncToNavidromeAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (configurationError is not null)
        {
            return configurationError;
        }

        if (navidrome is null)
        {
            return PlaylistSyncResult.Failed(NavidromeNotConfiguredMessage, PlaylistSyncResultKind.Retry);
        }

        var storedPlaylistId = NormalizeExistingTargetPlaylistId(existingPlaylistId);
        var playlistLookup = await ResolveAuthoritativeNavidromePlaylistIdAsync(
            navidrome,
            playlist,
            existingPlaylistId,
            cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return PlaylistSyncResult.Failed("Navidrome playlist lookup timed out.", PlaylistSyncResultKind.Retry);
        }

        existingPlaylistId = playlistLookup.Status == TargetLookupStatus.Success ? playlistLookup.Value : null;
        var playlistName = ResolvePlaylistName(playlist);
        if (!string.IsNullOrWhiteSpace(existingPlaylistId))
        {
            await SyncNavidromePlaylistMetadataAsync(navidrome, playlist, existingPlaylistId, cancellationToken);
        }

        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var navidromeMatches = await ResolveNavidromeItemIdsAsync(navidrome, tracks, orderedTrackIds, cancellationToken);
        var itemIds = navidromeMatches.Select(static item => item.TargetItemId).ToList();
        var matchSummary = new SyncMatchSummary(
            itemIds,
            navidromeMatches,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: itemIds.Count,
            MissingTracks: Math.Max(0, tracks.Count - itemIds.Count),
            MetadataMatches: 0,
            SearchMatches: itemIds.Count);
        if (itemIds.Count == 0)
        {
            var emptyPlaylistId = await EnsureNavidromePlaylistContainerAsync(
                navidrome,
                playlist,
                preference,
                existingPlaylistId,
                cancellationToken);
            return await CompleteTargetMembershipAsync(
                playlist,
                preference,
                NavidromeService,
                emptyPlaylistId,
                storedPlaylistId,
                matchSummary,
                Array.Empty<PlaylistWatchTargetMembership>(),
                tracks,
                orderedTrackIds,
                writeComplete: true,
                successBaseMessage: "No Navidrome matches found for this playlist.",
                applyArtwork: () => SyncNavidromePlaylistArtworkAsync(
                    navidrome,
                    playlist,
                    preference,
                    emptyPlaylistId ?? string.Empty,
                    cancellationToken),
                extraSuccessSuffix: null,
                cancellationToken);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        var playlistId = await _navidromeApiClient.CreateOrUpdatePlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistName,
            itemIds,
            existingPlaylistId,
            appendMissingOnly,
            cancellationToken,
            playlist.Description);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BuildWriteFailureResult(
                BuildSyncMessage("Failed to create or update the Navidrome playlist.", matchSummary),
                matchSummary);
        }

        await PersistTargetPlaylistBindingAsync(playlist, preference, NavidromeService, playlistId, cancellationToken);
        if (!appendMissingOnly)
        {
            await TryReorderNavidromePlaylistAsync(
                navidrome,
                playlistId,
                playlistName,
                playlist.Description,
                itemIds,
                cancellationToken);
        }

        var metadataSynced = string.IsNullOrWhiteSpace(existingPlaylistId)
            || !string.Equals(existingPlaylistId, playlistId, StringComparison.OrdinalIgnoreCase)
            ? await SyncNavidromePlaylistMetadataAsync(navidrome, playlist, playlistId, cancellationToken)
            : true;
        var verifiedMemberships = await ReadVerifiedNavidromeMembershipsAsync(
            navidrome,
            playlistId,
            navidromeMatches,
            cancellationToken);
        var modeLabel = appendMissingOnly ? "append" : "mirror";
        var fullSyncIssues = BuildNavidromeFullSyncIssues(metadataSynced);
        return await CompleteTargetMembershipAsync(
            playlist,
            preference,
            NavidromeService,
            playlistId,
            storedPlaylistId,
            matchSummary,
            verifiedMemberships,
            tracks,
            orderedTrackIds,
            writeComplete: true,
            successBaseMessage: $"Playlist synced to Navidrome ({modeLabel}).",
            applyArtwork: () => SyncNavidromePlaylistArtworkAsync(
                navidrome,
                playlist,
                preference,
                playlistId,
                cancellationToken),
            extraSuccessSuffix: fullSyncIssues.Count == 0 ? null : string.Join(" ", fullSyncIssues),
            cancellationToken);
    }

    private async Task<List<PlaylistWatchTargetMembership>> ReadVerifiedPlexMembershipsAsync(
        PlexConnection plex,
        string playlistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        CancellationToken cancellationToken)
    {
        var actualTargetIds = (await _plexApiClient.GetPlaylistItemsAsync(
                plex.Url,
                plex.Token,
                playlistId,
                cancellationToken))
            .Select(static item => item.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedMemberships
            .Where(item => actualTargetIds.Contains(item.TargetItemId))
            .ToList();
    }

    private async Task InvalidateConfirmedMissingPlexIdentitiesAsync(
        PlexConnection plex,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        IReadOnlyCollection<PlaylistWatchTargetMembership> verifiedMemberships,
        CancellationToken cancellationToken)
    {
        var verifiedLocalTrackIds = verifiedMemberships
            .Select(static membership => membership.LocalTrackId)
            .ToHashSet();
        var confirmedMissingLocalTrackIds = new List<long>();
        foreach (var membership in expectedMemberships.Where(membership =>
                     !verifiedLocalTrackIds.Contains(membership.LocalTrackId)))
        {
            var availability = await _plexApiClient.CheckTrackAvailabilityAsync(
                plex.Url,
                plex.Token,
                membership.TargetItemId,
                cancellationToken);
            if (availability == PlexItemAvailability.Missing)
            {
                confirmedMissingLocalTrackIds.Add(membership.LocalTrackId);
            }
        }

        if (confirmedMissingLocalTrackIds.Count > 0)
        {
            await _libraryRepository.DeleteMediaServerTrackMetadataAsync(
                PlexService,
                confirmedMissingLocalTrackIds,
                cancellationToken);
        }
    }

    private async Task<List<PlaylistWatchTargetMembership>> ReadVerifiedJellyfinMembershipsAsync(
        JellyfinConnection jellyfin,
        string playlistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        CancellationToken cancellationToken)
    {
        var actualTargetIds = (await _jellyfinApiClient.GetPlaylistEntriesAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                cancellationToken))
            .Select(static item => item.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verified = expectedMemberships
            .Where(item => actualTargetIds.Contains(item.TargetItemId))
            .ToList();
        await _libraryRepository.DeleteMediaServerTrackMetadataAsync(
            JellyfinService,
            expectedMemberships.Except(verified).Select(static item => item.LocalTrackId).ToList(),
            cancellationToken);
        return verified;
    }

    private async Task<List<PlaylistWatchTargetMembership>> ReadVerifiedNavidromeMembershipsAsync(
        NavidromeConnection navidrome,
        string playlistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        CancellationToken cancellationToken)
    {
        var actualTargetIds = (await _navidromeApiClient.GetPlaylistEntriesAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                playlistId,
                cancellationToken))
            .Select(static item => item.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verified = expectedMemberships
            .Where(item => actualTargetIds.Contains(item.TargetItemId))
            .ToList();
        await _libraryRepository.DeleteMediaServerTrackMetadataAsync(
            NavidromeService,
            expectedMemberships.Except(verified).Select(static item => item.LocalTrackId).ToList(),
            cancellationToken);
        return verified;
    }

    private async Task<string?> EnsureJellyfinPlaylistContainerAsync(
        JellyfinConnection jellyfin,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistName,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        string? playlistId;
        if (!string.IsNullOrWhiteSpace(existingPlaylistId))
        {
            playlistId = existingPlaylistId.Trim();
        }
        else
        {
            var nameLookup = await _jellyfinApiClient.FindPlaylistIdByNameResult(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistName,
                cancellationToken);
            if (nameLookup.Status == TargetLookupStatus.Transient)
            {
                return null;
            }

            playlistId = nameLookup.Status == TargetLookupStatus.Success ? nameLookup.Value : null;
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistName,
                Array.Empty<string>(),
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        await PersistTargetPlaylistBindingAsync(playlist, preference, JellyfinService, playlistId, cancellationToken);
        await SyncJellyfinPlaylistMetadataAsync(jellyfin, playlist, playlistId, cancellationToken);
        return playlistId;
    }

    private async Task<string?> EnsureNavidromePlaylistContainerAsync(
        NavidromeConnection navidrome,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string? existingPlaylistId,
        CancellationToken cancellationToken)
    {
        var playlistId = await _navidromeApiClient.CreateOrUpdatePlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            ResolvePlaylistName(playlist),
            Array.Empty<string>(),
            existingPlaylistId,
            appendMissingOnly: true,
            cancellationToken,
            playlist.Description);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return string.IsNullOrWhiteSpace(existingPlaylistId) ? null : existingPlaylistId.Trim();
        }

        await PersistTargetPlaylistBindingAsync(playlist, preference, NavidromeService, playlistId, cancellationToken);
        await SyncNavidromePlaylistMetadataAsync(navidrome, playlist, playlistId, cancellationToken);
        return playlistId;
    }

    private async Task<PlaylistSyncResult> CompleteTargetMembershipAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string targetService,
        string? playlistId,
        string? storedPlaylistId,
        SyncMatchSummary matchSummary,
        IReadOnlyCollection<PlaylistWatchTargetMembership> verifiedMemberships,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        bool writeComplete,
        string successBaseMessage,
        Func<Task<bool>> applyArtwork,
        string? extraSuccessSuffix,
        CancellationToken cancellationToken)
    {
        await PersistTargetMembershipRowsAsync(
            playlist,
            targetService,
            playlistId,
            matchSummary.Memberships,
            verifiedMemberships,
            tracks,
            orderedTrackIds,
            cancellationToken);

        var verifiedSummary = WithVerifiedMembershipCounts(matchSummary, verifiedMemberships.Count);
        var resolved = await ResolveVerifiedIdentityCountAsync(
            matchSummary,
            targetService,
            orderedTrackIds,
            cancellationToken);
        if (!IsResolvedMembershipVerified(resolved, verifiedMemberships.Count, writeComplete))
        {
            return PlaylistSyncResult.Failed(
                BuildSyncMessage(
                    string.Concat(FormatTargetServiceLabel(targetService), " playlist verification is incomplete; unresolved target identities will be refreshed and retried."),
                    verifiedSummary),
                PlaylistSyncResultKind.WriteLag,
                playlistId,
                verifiedMemberships.Count,
                verifiedSummary.SourceTracks,
                verifiedSummary.LocalMatches,
                verifiedSummary.TargetMatches,
                verifiedSummary.MissingTracks,
                verifiedSummary.MetadataMatches,
                verifiedSummary.SearchMatches);
        }

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            await TryApplyOrScheduleMembershipArtworkAsync(
                playlist,
                preference,
                targetService,
                playlistId,
                storedPlaylistId,
                applyArtwork,
                cancellationToken);
        }

        if (HasUnresolvedTargetIdentities(matchSummary.LocalMatches, resolved))
        {
            await RequestTargetLibraryRefreshAsync(targetService, cancellationToken);
            var gapMessage = BuildSyncMessage(successBaseMessage, verifiedSummary);
            if (!string.IsNullOrWhiteSpace(extraSuccessSuffix))
            {
                gapMessage = string.Concat(gapMessage, " ", extraSuccessSuffix);
            }

            return BuildIdentityGapResult(gapMessage, playlistId, verifiedSummary, verifiedMemberships.Count);
        }

        var successMessage = BuildSyncMessage(successBaseMessage, verifiedSummary);
        if (!string.IsNullOrWhiteSpace(extraSuccessSuffix))
        {
            successMessage = string.Concat(successMessage, " ", extraSuccessSuffix);
        }

        return BuildCompletedResult(successMessage, playlistId, verifiedSummary, verifiedMemberships.Count);
    }

    private async Task PersistTargetMembershipRowsAsync(
        PlaylistWatchlistDto playlist,
        string targetService,
        string? playlistId,
        IReadOnlyCollection<PlaylistWatchTargetMembership> expectedMemberships,
        IReadOnlyCollection<PlaylistWatchTargetMembership> verifiedMemberships,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var verifiedIds = verifiedMemberships
            .Select(static item => item.TrackSourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedIds = expectedMemberships
            .Select(static item => item.TrackSourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PlaylistWatchTargetMembershipWrite>(
            expectedMemberships.Count + tracks.Count);
        foreach (var membership in verifiedMemberships)
        {
            rows.Add(new PlaylistWatchTargetMembershipWrite(
                membership.TrackSourceId,
                membership.LocalTrackId,
                membership.TargetItemId,
                "playlist_synced"));
        }

        foreach (var membership in expectedMemberships)
        {
            if (verifiedIds.Contains(membership.TrackSourceId))
            {
                continue;
            }

            rows.Add(new PlaylistWatchTargetMembershipWrite(
                membership.TrackSourceId,
                membership.LocalTrackId,
                membership.TargetItemId,
                "waiting_for_target"));
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var sourceTrackId = tracks[index].SourceTrackId;
            if (string.IsNullOrWhiteSpace(sourceTrackId)
                || orderedTrackIds[index] <= 0
                || expectedIds.Contains(sourceTrackId))
            {
                continue;
            }

            rows.Add(new PlaylistWatchTargetMembershipWrite(
                sourceTrackId,
                orderedTrackIds[index],
                TargetItemId: null,
                "waiting_for_identity"));
        }

        await _libraryRepository.ReplacePlaylistWatchTargetMembershipAsync(
            playlist.Source,
            playlist.SourceId,
            targetService,
            playlistId,
            rows,
            cancellationToken);
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> SyncExistingJellyfinPlaylistItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        bool appendMissingOnly,
        CancellationToken cancellationToken)
    {
        var entries = await _jellyfinApiClient.GetPlaylistEntriesAsync(
            url,
            apiKey,
            userId,
            playlistId,
            cancellationToken);
        if (appendMissingOnly)
        {
            return await AppendMissingJellyfinItemsAsync(url, apiKey, userId, playlistId, itemIds, entries, cancellationToken);
        }

        return await ReplaceJellyfinPlaylistItemsAsync(url, apiKey, userId, playlistId, itemIds, entries, cancellationToken);
    }

    private static string? ResolveExistingTargetPlaylistId(PlaylistWatchPreferenceDto? preference, string service)
    {
        if (preference is null || string.IsNullOrWhiteSpace(service))
        {
            return null;
        }

        return service.Trim().ToLowerInvariant() switch
        {
            PlexService => NormalizeExistingTargetPlaylistId(preference.PlexPlaylistId),
            JellyfinService => NormalizeExistingTargetPlaylistId(preference.JellyfinPlaylistId),
            NavidromeService => NormalizeExistingTargetPlaylistId(preference.NavidromePlaylistId),
            _ => null
        };
    }

    private async Task<TargetPlaylistLookup<string>> ResolveAuthoritativePlexPlaylistIdAsync(
        PlexConnection connection,
        PlaylistWatchlistDto playlist,
        string? storedPlaylistId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(storedPlaylistId))
        {
            var byId = await _plexApiClient.GetPlaylistResult(
                connection.Url,
                connection.Token,
                storedPlaylistId,
                cancellationToken);
            if (byId.Status == TargetLookupStatus.Transient)
            {
                return TargetPlaylistLookup<string>.Unavailable(byId.HttpStatusCode);
            }

            if (byId.Status == TargetLookupStatus.Success && !string.IsNullOrWhiteSpace(byId.Value?.Id))
            {
                return TargetPlaylistLookup<string>.Found(byId.Value.Id, byId.HttpStatusCode);
            }
        }

        var playlists = await _plexApiClient.GetPlaylistsResult(
            connection.Url,
            connection.Token,
            cancellationToken);
        if (playlists.Status == TargetLookupStatus.Transient)
        {
            return TargetPlaylistLookup<string>.Unavailable(playlists.HttpStatusCode);
        }

        var playlistName = ResolvePlaylistName(playlist);
        var resolved = (playlists.Value ?? Array.Empty<PlexPlaylist>()).FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Id)
                && string.Equals(item.Title, playlistName, StringComparison.OrdinalIgnoreCase))?.Id;
        return string.IsNullOrWhiteSpace(resolved)
            ? TargetPlaylistLookup<string>.Missing()
            : TargetPlaylistLookup<string>.Found(resolved);
    }

    private async Task<TargetPlaylistLookup<string>> ResolveAuthoritativeJellyfinPlaylistIdAsync(
        JellyfinConnection connection,
        PlaylistWatchlistDto playlist,
        string? storedPlaylistId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(storedPlaylistId))
        {
            var existing = await _jellyfinApiClient.GetItemResult(
                connection.Url,
                connection.ApiKey,
                connection.UserId,
                storedPlaylistId,
                cancellationToken);
            if (existing.Status == TargetLookupStatus.Transient)
            {
                return TargetPlaylistLookup<string>.Unavailable(existing.HttpStatusCode);
            }

            if (existing.Status == TargetLookupStatus.Success && !string.IsNullOrWhiteSpace(existing.Value?.Id))
            {
                return TargetPlaylistLookup<string>.Found(existing.Value.Id, existing.HttpStatusCode);
            }
        }

        var byName = await _jellyfinApiClient.FindPlaylistIdByNameResult(
            connection.Url,
            connection.ApiKey,
            connection.UserId,
            ResolvePlaylistName(playlist),
            cancellationToken);
        if (byName.Status == TargetLookupStatus.Transient)
        {
            return TargetPlaylistLookup<string>.Unavailable(byName.HttpStatusCode);
        }

        return byName.Status == TargetLookupStatus.Success && !string.IsNullOrWhiteSpace(byName.Value)
            ? TargetPlaylistLookup<string>.Found(byName.Value, byName.HttpStatusCode)
            : TargetPlaylistLookup<string>.Missing(byName.HttpStatusCode);
    }

    private async Task<TargetPlaylistLookup<string>> ResolveAuthoritativeNavidromePlaylistIdAsync(
        NavidromeConnection connection,
        PlaylistWatchlistDto playlist,
        string? storedPlaylistId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(storedPlaylistId))
        {
            var existing = await _navidromeApiClient.GetPlaylistResult(
                connection.Url,
                connection.Username,
                connection.Password,
                storedPlaylistId,
                cancellationToken);
            if (existing.Status == TargetLookupStatus.Transient)
            {
                return TargetPlaylistLookup<string>.Unavailable(existing.HttpStatusCode);
            }

            if (existing.Status == TargetLookupStatus.Success && !string.IsNullOrWhiteSpace(existing.Value?.Id))
            {
                return TargetPlaylistLookup<string>.Found(existing.Value.Id, existing.HttpStatusCode);
            }
        }

        var byName = await _navidromeApiClient.FindPlaylistIdByNameResult(
            connection.Url,
            connection.Username,
            connection.Password,
            ResolvePlaylistName(playlist),
            cancellationToken);
        if (byName.Status == TargetLookupStatus.Transient)
        {
            return TargetPlaylistLookup<string>.Unavailable(byName.HttpStatusCode);
        }

        return byName.Status == TargetLookupStatus.Success && !string.IsNullOrWhiteSpace(byName.Value)
            ? TargetPlaylistLookup<string>.Found(byName.Value, byName.HttpStatusCode)
            : TargetPlaylistLookup<string>.Missing(byName.HttpStatusCode);
    }

    private static string? NormalizeExistingTargetPlaylistId(string? playlistId)
    {
        var normalized = (playlistId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private async Task PersistTargetPlaylistBindingAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string service,
        string? playlistId,
        CancellationToken cancellationToken)
    {
        if (preference is null
            || string.IsNullOrWhiteSpace(playlistId)
            || !string.Equals(playlist.Source, preference.Source, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(playlist.SourceId, preference.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _libraryRepository.UpdatePlaylistWatchTargetPlaylistIdAsync(
            playlist.Source,
            playlist.SourceId,
            service,
            playlistId,
            cancellationToken);
    }

    internal static PlaylistMembershipDelta ComputePlaylistMembershipDelta(
        IReadOnlyList<string> currentIds,
        IReadOnlyList<string> intendedIds,
        bool appendMissingOnly)
    {
        var intended = intendedIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var current = currentIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToList();
        var currentSet = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intendedSet = intended.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = intended.Where(id => !currentSet.Contains(id)).ToList();
        var toRemove = appendMissingOnly
            ? new List<string>()
            : current.Where(id => !intendedSet.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyList<string> after;
        if (appendMissingOnly)
        {
            after = current.Concat(toAdd).ToList();
        }
        else
        {
            var retained = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in current)
            {
                if (intendedSet.Contains(id) && seen.Add(id))
                {
                    retained.Add(id);
                }
            }

            after = retained.Concat(toAdd).ToList();
        }

        var needsReorder = !appendMissingOnly
            && intended.Count > 0
            && !after.SequenceEqual(intended, StringComparer.OrdinalIgnoreCase);
        return new PlaylistMembershipDelta(toAdd, toRemove, needsReorder, intended);
    }

    private async Task<JellyfinPlaylistMoveResult> TryReorderJellyfinPlaylistAsync(
        JellyfinConnection jellyfin,
        string playlistId,
        IReadOnlyList<string> intendedItemIds,
        CancellationToken cancellationToken)
    {
        if (!await IsTargetCapabilitySupportedAsync(JellyfinService, JellyfinPlaylistMoveCapability, cancellationToken))
        {
            return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.NotSupported, null);
        }

        var entries = await _jellyfinApiClient.GetPlaylistEntriesAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            cancellationToken);
        var currentIds = entries.Select(static entry => entry.ItemId).ToList();
        var delta = ComputePlaylistMembershipDelta(currentIds, intendedItemIds, appendMissingOnly: false);
        if (!delta.NeedsReorder)
        {
            return new JellyfinPlaylistMoveResult(JellyfinPlaylistMoveStatus.Moved, null);
        }

        var moved = await _jellyfinApiClient.ReorderPlaylistItemsAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            delta.IntendedOrder,
            entries,
            cancellationToken);
        if (moved.Status == JellyfinPlaylistMoveStatus.NotSupported)
        {
            _logger.LogInformation(
                "Jellyfin playlist move is unsupported for {PlaylistId}: HTTP {StatusCode}.",
                SafeLog(playlistId),
                moved.HttpStatusCode);
            await PersistTargetCapabilityAsync(
                JellyfinService,
                JellyfinPlaylistMoveCapability,
                supported: false,
                lastError: moved.HttpStatusCode is null ? "playlist move not supported" : $"HTTP {moved.HttpStatusCode}",
                cancellationToken);
        }

        return moved;
    }

    private async Task TryReorderNavidromePlaylistAsync(
        NavidromeConnection navidrome,
        string playlistId,
        string playlistName,
        string? playlistComment,
        IReadOnlyList<string> intendedItemIds,
        CancellationToken cancellationToken)
    {
        if (!await IsTargetCapabilitySupportedAsync(NavidromeService, NavidromeNativePlaylistPutCapability, cancellationToken))
        {
            return;
        }

        var entries = await _navidromeApiClient.GetPlaylistEntriesAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistId,
            cancellationToken);
        var currentIds = entries.Select(static entry => entry.ItemId).ToList();
        var delta = ComputePlaylistMembershipDelta(currentIds, intendedItemIds, appendMissingOnly: false);
        if (!delta.NeedsReorder)
        {
            return;
        }

        var put = await _navidromeApiClient.ReplaceNativePlaylistTracksAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistId,
            playlistName,
            playlistComment,
            delta.IntendedOrder,
            cancellationToken);
        if (put.Status == NavidromeNativePlaylistPutStatus.NotSupported && put.HttpStatusCode.HasValue)
        {
            _logger.LogInformation(
                "Navidrome native playlist PUT is unsupported for {PlaylistId}: HTTP {StatusCode}.",
                SafeLog(playlistId),
                put.HttpStatusCode);
            await PersistTargetCapabilityAsync(
                NavidromeService,
                NavidromeNativePlaylistPutCapability,
                supported: false,
                lastError: $"HTTP {put.HttpStatusCode}",
                cancellationToken);
        }
    }

    private async Task<bool> IsTargetCapabilitySupportedAsync(
        string targetService,
        string capability,
        CancellationToken cancellationToken)
    {
        var supported = await _libraryRepository.GetWatchlistTargetCapabilitySupportedAsync(
            targetService,
            capability,
            cancellationToken);
        return supported != false;
    }

    private Task PersistTargetCapabilityAsync(
        string targetService,
        string capability,
        bool supported,
        string? lastError,
        CancellationToken cancellationToken)
        => _libraryRepository.SetWatchlistTargetCapabilityAsync(
            targetService,
            capability,
            supported,
            lastError,
            cancellationToken);

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> AppendMissingJellyfinItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        IReadOnlyList<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        var existingItemIds = entries
            .Select(static entry => entry.ItemId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = itemIds
            .Where(trackId => !existingItemIds.Contains(trackId))
            .ToList();
        if (pending.Count == 0)
        {
            return (true, null, 0);
        }

        var appended = await _jellyfinApiClient.AddPlaylistItemsAsync(
            url,
            apiKey,
            userId,
            playlistId,
            pending,
            cancellationToken);
        if (!appended)
        {
            return (false, "Failed to append tracks to Jellyfin playlist.", 0);
        }

        return (true, null, pending.Count);
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> ReplaceJellyfinPlaylistItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        List<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        var expected = itemIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staleEntryIds = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.PlaylistEntryId))
            {
                continue;
            }

            if (!expected.Contains(entry.ItemId) || !retained.Add(entry.ItemId))
            {
                staleEntryIds.Add(entry.PlaylistEntryId);
            }
        }

        var pending = itemIds
            .Where(trackId => !string.IsNullOrWhiteSpace(trackId) && !retained.Contains(trackId))
            .ToList();
        if (staleEntryIds.Count == 0 && pending.Count == 0)
        {
            return (true, null, 0);
        }

        if (pending.Count > 0
            && !await _jellyfinApiClient.AddPlaylistItemsAsync(
                url,
                apiKey,
                userId,
                playlistId,
                pending,
                cancellationToken))
        {
            return (false, "Failed to add tracks to Jellyfin playlist.", 0);
        }

        if (staleEntryIds.Count > 0
            && !await _jellyfinApiClient.RemovePlaylistEntriesAsync(
                url,
                apiKey,
                userId,
                playlistId,
                staleEntryIds,
                cancellationToken))
        {
            return (false, "Failed to remove stale Jellyfin playlist items.", 0);
        }

        return (true, null, pending.Count);
    }

    private async Task<PlaylistSyncResult> SyncPlexPlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (plex == null)
        {
            return PlaylistSyncResult.Failed(PlexNotConfiguredMessage, PlaylistSyncResultKind.Retry);
        }

        var playlistLookup = await ResolveAuthoritativePlexPlaylistIdAsync(
            plex,
            playlist,
            ResolveExistingTargetPlaylistId(preference, PlexService),
            cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return PlaylistSyncResult.Failed("Plex playlist lookup timed out.", PlaylistSyncResultKind.Retry);
        }

        if (playlistLookup.Status == TargetLookupStatus.NotFound
            || string.IsNullOrWhiteSpace(playlistLookup.Value))
        {
            return await RecreateMissingTargetPlaylistAsync(
                playlist,
                preference,
                PlexService,
                cancellationToken);
        }

        var playlistId = playlistLookup.Value;

        var updated = await SyncPlexPlaylistArtworkAsync(
            plex,
            playlist,
            preference,
            playlistId,
            cancellationToken);
        if (updated)
        {
            await PersistTargetPlaylistBindingAsync(
                playlist,
                preference,
                PlexService,
                playlistId,
                cancellationToken);
        }

        return updated
            ? PlaylistSyncResult.Completed("Playlist artwork synced.", playlistId)
            : PlaylistSyncResult.Failed("Failed to sync Plex playlist artwork.", PlaylistSyncResultKind.Retry, playlistId);
    }

    private async Task<PlaylistSyncResult> SyncJellyfinPlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (jellyfin == null)
        {
            return PlaylistSyncResult.Failed(JellyfinNotConfiguredMessage, PlaylistSyncResultKind.Retry);
        }

        var playlistLookup = await ResolveAuthoritativeJellyfinPlaylistIdAsync(
            jellyfin,
            playlist,
            ResolveExistingTargetPlaylistId(preference, JellyfinService),
            cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return PlaylistSyncResult.Failed("Jellyfin playlist lookup timed out.", PlaylistSyncResultKind.Retry);
        }

        if (playlistLookup.Status == TargetLookupStatus.NotFound
            || string.IsNullOrWhiteSpace(playlistLookup.Value))
        {
            return await RecreateMissingTargetPlaylistAsync(
                playlist,
                preference,
                JellyfinService,
                cancellationToken);
        }

        var playlistId = playlistLookup.Value;

        var updated = await SyncJellyfinPlaylistArtworkAsync(
            jellyfin,
            playlist,
            preference,
            playlistId,
            cancellationToken);
        if (updated)
        {
            await PersistTargetPlaylistBindingAsync(
                playlist,
                preference,
                JellyfinService,
                playlistId,
                cancellationToken);
        }

        return updated
            ? PlaylistSyncResult.Completed("Playlist artwork synced.", playlistId)
            : PlaylistSyncResult.Failed("Failed to sync Jellyfin playlist artwork.", PlaylistSyncResultKind.Retry, playlistId);
    }

    private async Task<PlaylistSyncResult> SyncNavidromePlaylistArtworkOnlyAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (navidrome == null)
        {
            return PlaylistSyncResult.Failed(NavidromeNotConfiguredMessage, PlaylistSyncResultKind.Retry);
        }

        var playlistLookup = await ResolveAuthoritativeNavidromePlaylistIdAsync(
            navidrome,
            playlist,
            ResolveExistingTargetPlaylistId(preference, NavidromeService),
            cancellationToken);
        if (playlistLookup.Status == TargetLookupStatus.Transient)
        {
            return PlaylistSyncResult.Failed("Navidrome playlist lookup timed out.", PlaylistSyncResultKind.Retry);
        }

        if (playlistLookup.Status == TargetLookupStatus.NotFound
            || string.IsNullOrWhiteSpace(playlistLookup.Value))
        {
            return await RecreateMissingTargetPlaylistAsync(
                playlist,
                preference,
                NavidromeService,
                cancellationToken);
        }

        var playlistId = playlistLookup.Value;

        var updated = await SyncNavidromePlaylistArtworkAsync(
            navidrome,
            playlist,
            preference,
            playlistId,
            cancellationToken);
        if (updated)
        {
            await PersistTargetPlaylistBindingAsync(
                playlist,
                preference,
                NavidromeService,
                playlistId,
                cancellationToken);
        }

        return updated
            ? PlaylistSyncResult.Completed("Playlist artwork synced.", playlistId)
            : PlaylistSyncResult.Failed("Failed to sync Navidrome playlist artwork.", PlaylistSyncResultKind.Retry, playlistId);
    }

    private async Task<PlaylistSyncResult> RecreateMissingTargetPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string targetService,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Recreating {Target} playlist {Source}:{SourceId}; reason=target_gone.",
            FormatTargetServiceLabel(targetService),
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId));
        var cache = await _libraryRepository.GetPlaylistTrackCandidateCacheAsync(
            playlist.Source,
            playlist.SourceId,
            cancellationToken);
        IReadOnlyList<PlaylistTrackCandidate>? candidates = null;
        if (!string.IsNullOrWhiteSpace(cache?.CandidatesJson))
        {
            try
            {
                candidates = JsonSerializer.Deserialize<List<PlaylistTrackCandidate>>(
                    cache.CandidatesJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Stored playlist candidates could not be read while recreating {Target} playlist {Source}:{SourceId}.",
                    FormatTargetServiceLabel(targetService),
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId));
            }
        }

        return await SyncAvailablePlaylistTracksAsync(
            playlist,
            preference,
            candidates,
            targetService,
            force: false,
            cancellationToken);
    }

    private static bool ShouldSyncPlaylistArtwork(PlaylistWatchPreferenceDto? preference)
        => preference?.UpdateArtwork == true;

    /// <summary>
    /// Best-effort art push after membership. Never fails membership: durable art jobs handle retries.
    /// </summary>
    private async Task TryApplyOrScheduleMembershipArtworkAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string targetService,
        string targetPlaylistId,
        string? previousPlaylistId,
        Func<Task<bool>> applyArtwork,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return;
        }

        var revision = _playlistVisualService.GetTargetArtworkRevision(
            playlist.Source,
            playlist.SourceId,
            targetService);
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        var bindingChanged = !string.Equals(
            previousPlaylistId,
            targetPlaylistId,
            StringComparison.OrdinalIgnoreCase);
        var alreadyApplied = await _libraryRepository.IsPlaylistWatchArtworkRevisionAppliedAsync(
            playlist.Source,
            playlist.SourceId,
            targetService,
            revision,
            cancellationToken);
        if (!bindingChanged && alreadyApplied)
        {
            return;
        }

        var success = await applyArtwork();
        await _libraryRepository.SetPlaylistWatchArtworkTargetStateAsync(
            playlist.Source,
            playlist.SourceId,
            targetService,
            revision,
            success,
            success
                ? null
                : $"Initial playlist artwork failed for target playlist {targetPlaylistId}.",
            cancellationToken);
        if (!success)
        {
            await ScheduleArtworkForTargetAsync(
                playlist.Source,
                playlist.SourceId,
                targetService,
                cancellationToken);
        }
    }

    /// <summary>
    /// Enqueues durable artwork jobs for the active cached revision (gated by UpdateArtwork).
    /// </summary>
    public async Task<int> ScheduleArtworkForActiveRevisionAsync(
        string source,
        string sourceId,
        PlaylistWatchlistDto? playlist = null,
        PlaylistWatchPreferenceDto? preference = null,
        CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured
            || string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(sourceId))
        {
            return 0;
        }

        preference ??= await _libraryRepository.GetPlaylistWatchPreferenceAsync(
            source,
            sourceId,
            cancellationToken);
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return 0;
        }

        var targets = await ResolveTargetServicesAsync(preference, cancellationToken);
        if (targets.Count == 0)
        {
            return 0;
        }

        playlist ??= (await _libraryRepository.GetPlaylistWatchlistAsync(cancellationToken))
            .FirstOrDefault(item =>
                string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        var queued = 0;
        foreach (var targetService in targets)
        {
            var revision = _playlistVisualService.GetTargetArtworkRevision(
                source,
                sourceId,
                targetService);
            if (string.IsNullOrWhiteSpace(revision))
            {
                continue;
            }

            var recordedApplied = await _libraryRepository.IsPlaylistWatchArtworkRevisionAppliedAsync(
                source,
                sourceId,
                targetService,
                revision,
                cancellationToken);
            if (recordedApplied)
            {
                if (playlist is null)
                {
                    continue;
                }

                var targetCurrent = await IsPlaylistArtworkCurrentOnTargetAsync(
                    playlist,
                    preference,
                    targetService,
                    cancellationToken);
                if (targetCurrent)
                {
                    continue;
                }

                await _libraryRepository.SetPlaylistWatchArtworkTargetStateAsync(
                    source,
                    sourceId,
                    targetService,
                    revision,
                    false,
                    "The target playlist artwork is missing or stale.",
                    cancellationToken);
            }

            var job = await _libraryRepository.EnqueueWatchlistPlaylistArtworkSyncJobAsync(
                source,
                sourceId,
                targetService,
                revision,
                cancellationToken);
            if (job != null)
            {
                queued++;
            }
        }

        if (queued > 0)
        {
            _runSignal?.Request(WatchlistWakeReason.TargetSync);
        }

        return queued;
    }

    public async Task ScheduleArtworkForTargetAsync(
        string source,
        string sourceId,
        string targetService,
        CancellationToken cancellationToken = default)
    {
        if (!_libraryRepository.IsConfigured
            || string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(sourceId)
            || string.IsNullOrWhiteSpace(targetService))
        {
            return;
        }

        var preference = await _libraryRepository.GetPlaylistWatchPreferenceAsync(
            source,
            sourceId,
            cancellationToken);
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return;
        }

        var revision = _playlistVisualService.GetTargetArtworkRevision(
            source,
            sourceId,
            targetService);
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        if (await _libraryRepository.IsPlaylistWatchArtworkRevisionAppliedAsync(
                source,
                sourceId,
                targetService,
                revision,
                cancellationToken))
        {
            return;
        }

        var job = await _libraryRepository.EnqueueWatchlistPlaylistArtworkSyncJobAsync(
            source,
            sourceId,
            targetService,
            revision,
            cancellationToken);
        if (job != null)
        {
            _runSignal?.Request(WatchlistWakeReason.TargetSync);
        }
    }

    private async Task EnqueueCatchUpForNewlyResolvedIdentitiesAsync(
        string targetService,
        IReadOnlyCollection<MediaServerTrackMetadataUpsertDto> newlyResolved,
        CancellationToken cancellationToken)
    {
        foreach (var item in newlyResolved)
        {
            if (item.TrackId <= 0)
            {
                continue;
            }

            await _libraryRepository.EnqueueMembershipJobsForNewlyResolvedIdentityAsync(
                item.TrackId,
                targetService,
                string.Empty,
                cancellationToken);
        }
    }

    private async Task RequestTargetLibraryRefreshAsync(string targetService, CancellationToken cancellationToken)
    {
        var key = NormalizeService(targetService);
        var now = DateTimeOffset.UtcNow;
        if (_lastIdentityRefreshUtc.TryGetValue(key, out var last)
            && now - last < IdentityRefreshThrottle)
        {
            return;
        }

        _lastIdentityRefreshUtc[key] = now;
        try
        {
            await _mediaServerRefreshService.RequestLibraryRefreshAsync(targetService, cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogDebug(
                ex,
                "Media-server scan after unresolved {Target} playlist identities failed; membership already applied.",
                targetService);
        }
    }

    private async Task<int> ResolveVerifiedIdentityCountAsync(
        SyncMatchSummary matchSummary,
        string targetService,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var localIds = orderedTrackIds.Where(static id => id > 0).Distinct().ToList();
        var ledger = await _libraryRepository.CountResolvedSharedIdentitiesAsync(
            localIds,
            targetService,
            cancellationToken);
        return ledger.LedgerRowCount > 0 ? ledger.ResolvedCount : matchSummary.TargetMatches;
    }

    internal static bool IsResolvedMembershipVerified(
        int resolvedIdentities,
        int verifiedMembershipCount,
        bool writeComplete = true)
        => writeComplete && verifiedMembershipCount >= resolvedIdentities;

    internal static bool HasUnresolvedTargetIdentities(int sourceTracks, int intendedMembershipCount)
        => intendedMembershipCount < sourceTracks;

    private static SyncMatchSummary WithVerifiedMembershipCounts(
        SyncMatchSummary matchSummary,
        int verifiedMembershipCount)
        => matchSummary with
        {
            TargetMatches = verifiedMembershipCount,
            MissingTracks = Math.Max(0, matchSummary.SourceTracks - verifiedMembershipCount)
        };

    private async Task<bool> SyncPlexPlaylistArtworkAsync(
        PlexConnection plex,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return true;
        }

        var cachedVisual = _playlistVisualService.GetActiveStoredStillVisual(playlist.Source, playlist.SourceId);
        if (cachedVisual != null && File.Exists(cachedVisual.FilePath))
        {
            return await _plexApiClient.UpdatePlaylistPosterFromFileAsync(
                plex.Url,
                plex.Token,
                playlistId,
                cachedVisual.FilePath,
                cachedVisual.ContentType,
                cancellationToken);
        }

        _logger.LogWarning(
            "Plex playlist artwork cache is unavailable for {Source}:{SourceId}; the existing target artwork was preserved.",
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId));
        return false;
    }

    private async Task<bool> SyncJellyfinPlaylistArtworkAsync(
        JellyfinConnection jellyfin,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return true;
        }

        var cachedVisual = _playlistVisualService.GetActiveStoredStillVisual(playlist.Source, playlist.SourceId);
        if (cachedVisual != null)
        {
            var cachedUpdated = await _jellyfinApiClient.UpdateItemPrimaryImageFromFileAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                cachedVisual.FilePath,
                cachedVisual.ContentType,
                cancellationToken);
            if (!cachedUpdated)
            {
                _logger.LogWarning(
                    "Failed to update Jellyfin playlist artwork for {Source}:{SourceId} from cached local file {ImagePath}.",
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId),
                    SafeLog(cachedVisual.FilePath));
            }

            return cachedUpdated;
        }

        _logger.LogWarning(
            "Jellyfin playlist artwork cache is unavailable for {Source}:{SourceId}; the existing target artwork was preserved.",
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId));
        return false;
    }

    private async Task<bool> SyncJellyfinPlaylistMetadataAsync(
        JellyfinConnection jellyfin,
        PlaylistWatchlistDto playlist,
        string playlistId,
        CancellationToken cancellationToken)
    {
        var playlistName = ResolvePlaylistName(playlist);
        var description = string.IsNullOrWhiteSpace(playlist.Description) ? null : playlist.Description.Trim();
        var updated = await _jellyfinApiClient.UpdateItemMetadataAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            playlistName,
            description,
            cancellationToken);
        if (!updated)
        {
            _logger.LogWarning(
                "Failed to update Jellyfin playlist metadata for {Source}:{SourceId}.",
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId));
            return false;
        }

        var actual = await _jellyfinApiClient.GetItemAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistId,
            cancellationToken);
        var nameMatches = string.Equals(actual?.Name?.Trim(), playlistName, StringComparison.Ordinal);
        var descriptionMatches = string.IsNullOrWhiteSpace(description)
            || string.Equals((actual?.Overview ?? string.Empty).Trim(), description, StringComparison.Ordinal);
        if (nameMatches && descriptionMatches)
        {
            return true;
        }

        _logger.LogWarning(
            "Jellyfin playlist metadata verification failed for {Source}:{SourceId}. Expected name='{Name}', description length={DescriptionLength}; actual name='{ActualName}', description length={ActualDescriptionLength}.",
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId),
            SafeLog(playlistName),
            description?.Length ?? 0,
            SafeLog(actual?.Name),
            actual?.Overview?.Length ?? 0);
        return false;
    }

    private static List<string> BuildJellyfinFullSyncIssues(bool metadataSynced)
    {
        var issues = new List<string>();
        if (!metadataSynced)
        {
            issues.Add("playlist metadata did not verify.");
        }

        return issues;
    }

    private async Task<bool> SyncNavidromePlaylistArtworkAsync(
        NavidromeConnection navidrome,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (!ShouldSyncPlaylistArtwork(preference))
        {
            return true;
        }

        var animatedVisual = await _playlistVisualService.ResolveApplePlaylistAnimatedVisualAsync(
            playlist.Source,
            playlist.SourceId,
            cancellationToken);
        if (animatedVisual != null)
        {
            var updated = await _navidromeApiClient.UpdatePlaylistImageFromFileAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                playlistId,
                animatedVisual.FilePath,
                animatedVisual.ContentType,
                cancellationToken);
            return updated;
        }

        var cachedVisual = _playlistVisualService.GetActiveStoredStillVisual(playlist.Source, playlist.SourceId);
        if (cachedVisual != null)
        {
            var cachedUpdated = await _navidromeApiClient.UpdatePlaylistImageFromFileAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                playlistId,
                cachedVisual.FilePath,
                cachedVisual.ContentType,
                cancellationToken);
            if (!cachedUpdated)
            {
                _logger.LogWarning(
                    "Failed to update Navidrome playlist artwork for {Source}:{SourceId} from cached local file {ImagePath}.",
                    SafeLog(playlist.Source),
                    SafeLog(playlist.SourceId),
                    SafeLog(cachedVisual.FilePath));
            }

            return cachedUpdated;
        }

        _logger.LogWarning(
            "Navidrome playlist artwork cache is unavailable for {Source}:{SourceId}; the existing target artwork was preserved.",
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId));
        return false;
    }

    private async Task<bool> SyncNavidromePlaylistMetadataAsync(
        NavidromeConnection navidrome,
        PlaylistWatchlistDto playlist,
        string playlistId,
        CancellationToken cancellationToken)
    {
        var playlistName = ResolvePlaylistName(playlist);
        var description = string.IsNullOrWhiteSpace(playlist.Description) ? null : playlist.Description.Trim();
        var updated = await _navidromeApiClient.UpdatePlaylistMetadataAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistId,
            playlistName,
            description,
            cancellationToken);
        if (!updated)
        {
            _logger.LogWarning(
                "Failed to update Navidrome playlist metadata for {Source}:{SourceId}.",
                SafeLog(playlist.Source),
                SafeLog(playlist.SourceId));
            return false;
        }

        var actual = await _navidromeApiClient.GetPlaylistAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            playlistId,
            cancellationToken);
        var nameMatches = string.Equals(actual?.Name?.Trim(), playlistName, StringComparison.Ordinal);
        var descriptionMatches = string.IsNullOrWhiteSpace(description)
            || string.Equals((actual?.Comment ?? string.Empty).Trim(), description, StringComparison.Ordinal);
        if (nameMatches && descriptionMatches)
        {
            return true;
        }

        _logger.LogWarning(
            "Navidrome playlist metadata verification failed for {Source}:{SourceId}. Expected name='{Name}', description length={DescriptionLength}; actual name='{ActualName}', description length={ActualDescriptionLength}.",
            SafeLog(playlist.Source),
            SafeLog(playlist.SourceId),
            SafeLog(playlistName),
            description?.Length ?? 0,
            SafeLog(actual?.Name),
            actual?.Comment?.Length ?? 0);
        return false;
    }

    private static List<string> BuildNavidromeFullSyncIssues(bool metadataSynced)
    {
        var issues = new List<string>();
        if (!metadataSynced)
        {
            issues.Add("playlist metadata did not verify.");
        }

        return issues;
    }

    private static string SafeLog(string? value)
    {
        return DeezSpoTag.Core.Security.LogSanitizer.OneLine(value);
    }

    private static bool IsAbsoluteHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<SyncTrackSummary>> FilterTracksForSyncAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return new List<SyncTrackSummary>();
        }

        var normalizedSource = NormalizeSource(playlist.Source);
        var ignoredTrackIds = await _libraryRepository.GetPlaylistWatchIgnoredTrackIdsAsync(
            normalizedSource,
            playlist.SourceId,
            cancellationToken);
        var globalRules = await GetGlobalPlaylistBlockRulesAsync(cancellationToken);
        var effectiveBlockRules = PlaylistTrackBlockRuleHelper.MergeRules(preference?.IgnoreRules, globalRules);
        if (ignoredTrackIds.Count == 0 && (effectiveBlockRules == null || effectiveBlockRules.Count == 0))
        {
            return tracks.ToList();
        }

        return tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.SourceTrackId))
            .Where(track => !ignoredTrackIds.Contains(track.SourceTrackId))
            .Where(track => !ShouldBlockTrack(track, effectiveBlockRules))
            .ToList();
    }

    private async Task<IReadOnlyList<PlaylistTrackBlockRule>> GetGlobalPlaylistBlockRulesAsync(CancellationToken cancellationToken)
    {
        var preferences = await _libraryRepository.GetPlaylistWatchPreferencesAsync(cancellationToken);
        return PlaylistTrackBlockRuleHelper.BuildGlobalRules(preferences);
    }

    private static bool ShouldBlockTrack(SyncTrackSummary track, List<PlaylistTrackBlockRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        return rules.Any(rule =>
            PlaylistTrackBlockRuleMatcher.RuleMatches(
                new PlaylistTrackBlockRuleMatcher.TrackRuleMatchInput(
                    track.Name,
                    track.Artists,
                    track.Album,
                    track.Genres,
                    track.Explicit,
                    track.ReleaseDate),
                rule.ConditionField,
                rule.ConditionOperator,
                rule.ConditionValue));
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

    private static string NormalizeSyncMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == SyncModeAppend ? SyncModeAppend : SyncModeMirror;
    }

    private static string NormalizeService(string? service)
    {
        return (service ?? string.Empty).Trim().ToLowerInvariant();
    }

    private async Task<string> ResolveTargetServiceAsync(
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        if (preference?.SyncTargets is { Count: > 0 })
        {
            var configuredTargets = NormalizeTargetServices(preference.SyncTargets);
            if (configuredTargets.Count > 0)
            {
                return configuredTargets[0];
            }
        }

        var configuredService = NormalizeService(preference?.Service);
        if (string.Equals(configuredService, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(configuredService))
        {
            return configuredService;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = await _authService.LoadAsync();
        if (state.Plex is not null
            && !string.IsNullOrWhiteSpace(state.Plex.Url)
            && !string.IsNullOrWhiteSpace(state.Plex.Token)
            && !string.IsNullOrWhiteSpace(state.Plex.MachineIdentifier))
        {
            return PlexService;
        }

        if (state.Jellyfin is not null
            && !string.IsNullOrWhiteSpace(state.Jellyfin.Url)
            && !string.IsNullOrWhiteSpace(state.Jellyfin.ApiKey)
            && !string.IsNullOrWhiteSpace(state.Jellyfin.UserId))
        {
            return JellyfinService;
        }

        if (state.Navidrome is not null
            && !string.IsNullOrWhiteSpace(state.Navidrome.Url)
            && !string.IsNullOrWhiteSpace(state.Navidrome.Username)
            && !string.IsNullOrWhiteSpace(state.Navidrome.Password))
        {
            return NavidromeService;
        }

        return string.Empty;
    }

    private async Task<IReadOnlyList<string>> ResolveTargetServicesAsync(
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        if (preference?.SyncTargets is not null)
        {
            return NormalizeTargetServices(preference.SyncTargets);
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        return string.IsNullOrWhiteSpace(service)
            ? []
            : [service];
    }

    private static IReadOnlyList<string> NormalizeTargetServices(IEnumerable<string>? services)
    {
        if (services is null)
        {
            return [];
        }

        var normalized = new List<string>();
        foreach (var serviceValue in services)
        {
            var service = NormalizeService(serviceValue);
            if (string.IsNullOrWhiteSpace(service)
                || string.Equals(service, "none", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(service, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(service, PlexService, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, JellyfinService, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, NavidromeService, StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(service);
            }
        }

        return normalized;
    }

    private static string NormalizeSource(string? source)
    {
        return (source ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string ResolvePlaylistName(PlaylistWatchlistDto playlist)
    {
        var name = (playlist.Name ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Playlist" : name;
    }

    private async Task<(PlexConnection? Plex, PlaylistSyncResult? Error)> TryLoadConfiguredPlexAsync()
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return (null, PlaylistSyncResult.Failed(PlexNotConfiguredMessage, PlaylistSyncResultKind.Retry));
        }

        if (string.IsNullOrWhiteSpace(plex.MachineIdentifier))
        {
            return (null, PlaylistSyncResult.Failed("Plex machine identifier missing.", PlaylistSyncResultKind.Retry));
        }

        return (new PlexConnection(plex.Url, plex.Token, plex.MachineIdentifier), null);
    }

    private async Task<(JellyfinConnection? Jellyfin, PlaylistSyncResult? Error)> TryLoadConfiguredJellyfinAsync()
    {
        var state = await _authService.LoadAsync();
        var jellyfin = state.Jellyfin;
        if (jellyfin is null || string.IsNullOrWhiteSpace(jellyfin.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return (null, PlaylistSyncResult.Failed(JellyfinNotConfiguredMessage, PlaylistSyncResultKind.Retry));
        }

        if (string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            return (null, PlaylistSyncResult.Failed("Jellyfin user id is missing.", PlaylistSyncResultKind.Retry));
        }

        return (new JellyfinConnection(jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId), null);
    }

    private async Task<(NavidromeConnection? Navidrome, PlaylistSyncResult? Error)> TryLoadConfiguredNavidromeAsync()
    {
        var state = await _authService.LoadAsync();
        var navidrome = state.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            return (null, PlaylistSyncResult.Failed(NavidromeNotConfiguredMessage, PlaylistSyncResultKind.Retry));
        }

        return (new NavidromeConnection(navidrome.Url, navidrome.Username, navidrome.Password), null);
    }

    private async Task<List<long>> ResolveOrderedTrackIdsAsync(
        string playlistSource,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        return await ResolveLocalTrackIdsAsync(playlistSource, tracks, cancellationToken);
    }

    private async Task<List<long>> ResolveLocalTrackIdsAsync(
        string playlistSource,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var inputs = BuildLocalTrackIdentityInputs(playlistSource, tracks);
        var identities = await _libraryRepository.ResolveLocalTrackIdentitiesAsync(
            inputs,
            cancellationToken,
            audioVariant: "stereo_preferred");
        var resolved = new List<LibraryRepository.LocalTrackIdentityResult>(identities.Count);
        for (var index = 0; index < identities.Count; index++)
        {
            resolved.Add(await _localIdentityResolver.ResolveAsync(inputs[index], identities[index], cancellationToken));
        }
        return resolved
            .Select(static decision => decision.IsAmbiguous ? 0L : decision.LocalTrackId ?? 0L)
            .ToList();
    }

    private static List<LibraryRepository.LibraryExistenceInput> BuildLocalTrackIdentityInputs(
        string playlistSource,
        IReadOnlyList<SyncTrackSummary> tracks)
        => tracks
            .Select(track =>
            {
                var (identitySource, identityTrackId) = ResolveTrackIdentity(playlistSource, track);
                return new LibraryRepository.LibraryExistenceInput(
                    track.Isrc,
                    track.Name,
                    track.Artists,
                    track.DurationMs,
                    identitySource,
                    identityTrackId,
                    track.Album,
                    track.Explicit);
            })
            .ToList();

    private async Task<long?> ResolveLocalTrackIdAsync(
        string playlistSource,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (identitySource, identityTrackId) = ResolveTrackIdentity(playlistSource, track);
        var input = new LibraryRepository.LibraryExistenceInput(
                track.Isrc,
                track.Name,
                track.Artists,
                track.DurationMs,
                identitySource,
                identityTrackId,
                track.Album,
                track.Explicit);
        var initial = await _libraryRepository.ResolveLocalTrackIdentityAsync(
            input,
            cancellationToken: cancellationToken);
        var decision = await _localIdentityResolver.ResolveAsync(input, initial, cancellationToken);
        return decision.IsAmbiguous ? null : decision.LocalTrackId;
    }

    private async Task<PlaylistTrackSyncReadiness> CheckPlexTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, PlexService, localTrackId);
        }

        if (plex == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, PlexNotConfiguredMessage, PlexService, localTrackId);
        }

        var mapped = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            PlexService,
            new[] { localTrackId },
            cancellationToken);
        if (mapped.TryGetValue(localTrackId, out var mappedRatingKey)
            && !string.IsNullOrWhiteSpace(mappedRatingKey))
        {
            return new PlaylistTrackSyncReadiness(
                true,
                false,
                "Track is visible in Plex metadata mapping.",
                PlexService,
                localTrackId,
                mappedRatingKey);
        }

        var filePath = await _libraryRepository.GetTrackPrimaryFilePathAsync(localTrackId, cancellationToken);
        var identity = await _libraryRepository.GetLocalTrackIdentityAsync(localTrackId, cancellationToken);
        var searchTrack = EnrichSearchTrackFromLocalIdentity(track, identity);
        var ratingKey = await ResolvePlexRatingKeyAsync(
            plex,
            searchTrack,
            filePath,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(ratingKey))
        {
            return new PlaylistTrackSyncReadiness(
                false,
                false,
                "Track is not visible in Plex yet.",
                PlexService,
                localTrackId);
        }

        var plexMetadata = new[]
        {
            new MediaServerTrackMetadataUpsertDto(
                localTrackId,
                PlexService,
                ratingKey,
                filePath,
                DateTimeOffset.UtcNow)
        };
        await _libraryRepository.UpsertMediaServerTrackMetadataAsync(plexMetadata, cancellationToken);
        await EnqueueCatchUpForNewlyResolvedIdentitiesAsync(PlexService, plexMetadata, cancellationToken);
        return new PlaylistTrackSyncReadiness(
            true,
            false,
            "Track is visible in Plex search.",
            PlexService,
            localTrackId,
            ratingKey);
    }

    private async Task<PlaylistTrackSyncReadiness> CheckJellyfinTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, JellyfinService, localTrackId);
        }

        if (jellyfin == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, JellyfinNotConfiguredMessage, JellyfinService, localTrackId);
        }

        var mapped = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            JellyfinService,
            new[] { localTrackId },
            cancellationToken);
        if (mapped.TryGetValue(localTrackId, out var cachedItemId)
            && !string.IsNullOrWhiteSpace(cachedItemId))
        {
            return new PlaylistTrackSyncReadiness(
                true,
                false,
                "Track is visible in Jellyfin metadata mapping.",
                JellyfinService,
                localTrackId,
                cachedItemId);
        }

        var filePath = await _libraryRepository.GetTrackPrimaryFilePathAsync(localTrackId, cancellationToken);
        var identity = await _libraryRepository.GetLocalTrackIdentityAsync(localTrackId, cancellationToken);
        var searchTrack = EnrichSearchTrackFromLocalIdentity(track, identity);
        var itemId = await ResolveJellyfinItemIdAsync(
            jellyfin,
            searchTrack,
            filePath,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            var jellyfinMetadata = new[]
            {
                new MediaServerTrackMetadataUpsertDto(
                    localTrackId,
                    JellyfinService,
                    itemId,
                    filePath,
                    DateTimeOffset.UtcNow)
            };
            await _libraryRepository.UpsertMediaServerTrackMetadataAsync(jellyfinMetadata, cancellationToken);
            await EnqueueCatchUpForNewlyResolvedIdentitiesAsync(JellyfinService, jellyfinMetadata, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(itemId)
            ? new PlaylistTrackSyncReadiness(false, false, "Track is not visible in Jellyfin yet.", JellyfinService, localTrackId)
            : new PlaylistTrackSyncReadiness(true, false, "Track is visible in Jellyfin search.", JellyfinService, localTrackId, itemId);
    }

    private async Task<PlaylistTrackSyncReadiness> CheckNavidromeTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (navidrome, configurationError) = await TryLoadConfiguredNavidromeAsync();
        if (configurationError is not null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, NavidromeService, localTrackId);
        }

        var mapped = await _libraryRepository.GetMediaServerItemIdsByTrackIdsAsync(
            NavidromeService,
            new[] { localTrackId },
            cancellationToken);
        if (mapped.TryGetValue(localTrackId, out var cachedItemId)
            && !string.IsNullOrWhiteSpace(cachedItemId))
        {
            return new PlaylistTrackSyncReadiness(
                true,
                false,
                "Track is visible in Navidrome metadata mapping.",
                NavidromeService,
                localTrackId,
                cachedItemId);
        }

        var filePath = await _libraryRepository.GetTrackPrimaryFilePathAsync(localTrackId, cancellationToken);
        var identity = await _libraryRepository.GetLocalTrackIdentityAsync(localTrackId, cancellationToken);
        var searchTrack = EnrichSearchTrackFromLocalIdentity(track, identity);
        var itemId = await ResolveNavidromeItemIdAsync(
            navidrome!,
            searchTrack,
            filePath,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            var navidromeMetadata = new[]
            {
                new MediaServerTrackMetadataUpsertDto(
                    localTrackId,
                    NavidromeService,
                    itemId,
                    filePath,
                    DateTimeOffset.UtcNow)
            };
            await _libraryRepository.UpsertMediaServerTrackMetadataAsync(navidromeMetadata, cancellationToken);
            await EnqueueCatchUpForNewlyResolvedIdentitiesAsync(NavidromeService, navidromeMetadata, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(itemId)
            ? new PlaylistTrackSyncReadiness(false, false, "Track is not visible in Navidrome yet.", NavidromeService, localTrackId)
            : new PlaylistTrackSyncReadiness(true, false, "Track is visible in Navidrome search.", NavidromeService, localTrackId, itemId);
    }

    private async Task<SyncMatchSummary> ResolvePlexRatingKeysAsync(
        PlexConnection plex,
        IReadOnlyList<SyncTrackSummary> tracks,
        List<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var resolved = await ResolveSharedTargetIdentitiesAsync(
            PlexService,
            tracks,
            orderedTrackIds,
            async (item, track, filePath, ct) => await ResolvePlexRatingKeyAsync(
                plex,
                track,
                filePath,
                searchCache,
                ct),
            async (targetItemId, ct) =>
            {
                var availability = await _plexApiClient.CheckTrackAvailabilityAsync(
                    plex.Url,
                    plex.Token,
                    targetItemId,
                    ct);
                return availability == PlexItemAvailability.Missing;
            },
            cancellationToken);
        return BuildResolvedMatchSummary(tracks, orderedTrackIds, resolved);
    }

    private async Task<string?> ResolvePlexRatingKeyAsync(
        PlexConnection plex,
        SyncTrackSummary track,
        string? localFilePath,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await _plexApiClient.SearchTracksAsync(
            plex.Url,
            plex.Token,
            query,
            cancellationToken);

        var match = SelectBestMediaServerMatch(
            results,
            track,
            localFilePath,
            static result => result.FilePath,
            static result => result.RatingKey,
            IsTitleArtistMatch,
            static (candidate, result) => IsDurationMatch(candidate.DurationMs, result.DurationMs));

        var ratingKey = match?.RatingKey;
        cache[query] = ratingKey;
        return ratingKey;
    }

    private async Task<List<PlaylistWatchTargetMembership>> ResolveJellyfinItemIdsAsync(
        JellyfinConnection jellyfin,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var resolved = await ResolveSharedTargetIdentitiesAsync(
            JellyfinService,
            tracks,
            orderedTrackIds,
            async (item, track, filePath, ct) => await ResolveJellyfinItemIdAsync(
                jellyfin,
                track,
                filePath,
                searchCache,
                ct),
            confirmMissing: null,
            cancellationToken);
        return BuildResolvedMemberships(tracks, orderedTrackIds, resolved);
    }

    private async Task<string?> ResolveJellyfinItemIdAsync(
        JellyfinConnection jellyfin,
        SyncTrackSummary track,
        string? localFilePath,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await SearchJellyfinTrackCandidatesAsync(jellyfin, track, query, cache, cancellationToken);

        var match = SelectBestMediaServerMatch(
            results,
            track,
            localFilePath,
            static result => result.FilePath,
            static result => result.Id,
            IsTitleArtistMatch,
            static (candidate, result) => IsDurationMatch(candidate.DurationMs, result.DurationMs));

        var itemId = match?.Id;
        cache[query] = itemId;
        return itemId;
    }

    private async Task<List<JellyfinAudioTrack>> SearchJellyfinTrackCandidatesAsync(
        JellyfinConnection jellyfin,
        SyncTrackSummary track,
        string primaryQuery,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        var candidates = new List<JellyfinAudioTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildServerSearchQueries(track, primaryQuery))
        {
            if (cache.TryGetValue(query, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                candidates.Add(new JellyfinAudioTrack(cached, track.Name, track.Artists, track.DurationMs));
                continue;
            }

            var results = await _jellyfinApiClient.SearchTracksAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                query,
                cancellationToken);
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Id) && seen.Add(result.Id))
                {
                    candidates.Add(result);
                }
            }

            if (results.Count == 0)
            {
                cache[query] = null;
            }
        }

        return candidates;
    }

    private async Task<List<PlaylistWatchTargetMembership>> ResolveNavidromeItemIdsAsync(
        NavidromeConnection navidrome,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var resolved = await ResolveSharedTargetIdentitiesAsync(
            NavidromeService,
            tracks,
            orderedTrackIds,
            async (item, track, filePath, ct) => await ResolveNavidromeItemIdAsync(
                navidrome,
                track,
                filePath,
                searchCache,
                ct),
            confirmMissing: null,
            cancellationToken);
        return BuildResolvedMemberships(tracks, orderedTrackIds, resolved);
    }

    private async Task<IReadOnlyList<SharedIdentityResolveResult>> ResolveSharedTargetIdentitiesAsync(
        string targetService,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        Func<SharedIdentityResolveItem, SyncTrackSummary, string?, CancellationToken, Task<string?>> search,
        Func<string, CancellationToken, Task<bool>>? confirmMissing,
        CancellationToken cancellationToken)
    {
        var items = new List<SharedIdentityResolveItem>();
        var trackByLocalId = new Dictionary<long, SyncTrackSummary>();
        var seen = new HashSet<long>();
        for (var index = 0; index < tracks.Count && index < orderedTrackIds.Count; index++)
        {
            var localTrackId = orderedTrackIds[index];
            if (localTrackId <= 0 || !seen.Add(localTrackId))
            {
                continue;
            }

            items.Add(new SharedIdentityResolveItem(
                localTrackId,
                FilePath: null,
                SearchName: tracks[index].Name,
                SearchArtists: tracks[index].Artists));
            trackByLocalId[localTrackId] = tracks[index];
        }

        if (items.Count == 0)
        {
            return [];
        }

        var filePaths = await _libraryRepository.GetTrackPrimaryFilePathsAsync(
            items.Select(static item => item.LocalTrackId).ToList(),
            cancellationToken);
        items = items
            .Select(item =>
            {
                filePaths.TryGetValue(item.LocalTrackId, out var filePath);
                return item with { FilePath = filePath };
            })
            .ToList();

        return await _sharedIdentityResolver.ResolveAsync(
            targetService,
            items,
            async (item, ct) =>
            {
                var sourceTrack = trackByLocalId.TryGetValue(item.LocalTrackId, out var track)
                    ? track
                    : new SyncTrackSummary(
                        string.Empty,
                        null,
                        item.SearchName ?? string.Empty,
                        item.SearchArtists ?? string.Empty,
                        string.Empty,
                        null,
                        null,
                        Array.Empty<string>(),
                        null);
                var identity = await _libraryRepository.GetLocalTrackIdentityAsync(item.LocalTrackId, ct);
                var searchTrack = EnrichSearchTrackFromLocalIdentity(sourceTrack, identity);
                return await search(item, searchTrack, item.FilePath, ct);
            },
            confirmMissing,
            confirmExisting: false,
            currentRevision: string.Empty,
            requestRefresh: RequestTargetLibraryRefreshAsync,
            cancellationToken);
    }

    private static List<PlaylistWatchTargetMembership> BuildResolvedMemberships(
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        IReadOnlyList<SharedIdentityResolveResult> resolved)
    {
        var byLocalId = resolved
            .Where(static item =>
                item.LocalTrackId > 0
                && !string.IsNullOrWhiteSpace(item.TargetItemId)
                && string.Equals(item.Status, SharedIdentityResolver.StatusResolved, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static item => item.LocalTrackId, static item => item.TargetItemId!);
        var memberships = new List<PlaylistWatchTargetMembership>();
        for (var index = 0; index < tracks.Count && index < orderedTrackIds.Count; index++)
        {
            var localTrackId = orderedTrackIds[index];
            if (localTrackId <= 0
                || string.IsNullOrWhiteSpace(tracks[index].SourceTrackId)
                || !byLocalId.TryGetValue(localTrackId, out var targetItemId))
            {
                continue;
            }

            memberships.Add(new PlaylistWatchTargetMembership(
                tracks[index].SourceTrackId,
                localTrackId,
                targetItemId));
        }

        return memberships;
    }

    private static SyncMatchSummary BuildResolvedMatchSummary(
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        IReadOnlyList<SharedIdentityResolveResult> resolved)
    {
        var memberships = BuildResolvedMemberships(tracks, orderedTrackIds, resolved);
        var targetIds = memberships.Select(static item => item.TargetItemId).ToList();
        return new SyncMatchSummary(
            targetIds,
            memberships,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: targetIds.Count,
            MissingTracks: Math.Max(0, tracks.Count - targetIds.Count),
            MetadataMatches: resolved.Count(static item =>
                !item.Searched && !string.IsNullOrWhiteSpace(item.TargetItemId)),
            SearchMatches: resolved.Count(static item =>
                item.Searched && !string.IsNullOrWhiteSpace(item.TargetItemId)));
    }

    private async Task<string?> ResolveNavidromeItemIdAsync(
        NavidromeConnection navidrome,
        SyncTrackSummary track,
        string? localFilePath,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await SearchNavidromeTrackCandidatesAsync(navidrome, track, query, cache, cancellationToken);
        var match = SelectBestMediaServerMatch(
            results,
            track,
            localFilePath,
            static result => result.FilePath,
            static result => result.Id,
            IsTitleArtistMatch,
            static (candidate, result) => IsDurationMatch(candidate.DurationMs, result.DurationMs));
        var itemId = match?.Id;
        cache[query] = itemId;
        return itemId;
    }

    private static SyncTrackSummary EnrichSearchTrackFromLocalIdentity(
        SyncTrackSummary source,
        LocalTrackIdentityDto? identity)
    {
        if (identity is null)
        {
            return source;
        }

        return new SyncTrackSummary(
            source.SourceTrackId,
            string.IsNullOrWhiteSpace(identity.Isrc) ? source.Isrc : identity.Isrc,
            string.IsNullOrWhiteSpace(identity.Title) ? source.Name : identity.Title,
            string.IsNullOrWhiteSpace(identity.Artist) ? source.Artists : identity.Artist,
            string.IsNullOrWhiteSpace(identity.Album) ? source.Album : identity.Album,
            source.ReleaseDate,
            source.Explicit,
            source.Genres,
            identity.DurationMs ?? source.DurationMs,
            source.IdentitySource,
            source.IdentityTrackId);
    }

    private static TResult? SelectBestMediaServerMatch<TResult>(
        IReadOnlyList<TResult> results,
        SyncTrackSummary track,
        string? localFilePath,
        Func<TResult, string?> filePathSelector,
        Func<TResult, string?> idSelector,
        Func<SyncTrackSummary, TResult, bool> titleArtistMatch,
        Func<SyncTrackSummary, TResult, bool> durationMatch)
    {
        if (results.Count == 0)
        {
            return default;
        }

        // Prefer the candidate whose path matches the DeezSpoTag-indexed file for this local track id.
        if (!string.IsNullOrWhiteSpace(localFilePath))
        {
            var pathMatch = results.FirstOrDefault(result =>
                !string.IsNullOrWhiteSpace(idSelector(result))
                && MediaServerPathsReferToSameFile(localFilePath, filePathSelector(result)));
            if (pathMatch is not null)
            {
                return pathMatch;
            }
        }

        return results.FirstOrDefault(result =>
            !string.IsNullOrWhiteSpace(idSelector(result))
            && titleArtistMatch(track, result)
            && durationMatch(track, result));
    }

    internal static bool MediaServerPathsReferToSameFile(string? localPath, string? serverPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(serverPath))
        {
            return false;
        }

        var left = NormalizeMediaServerPath(localPath);
        var right = NormalizeMediaServerPath(serverPath);
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Tolerate different mount roots (/music/... vs /data/media/...) by matching trailing path.
        var longer = left.Length >= right.Length ? left : right;
        var shorter = left.Length >= right.Length ? right : left;
        if (longer.EndsWith("/" + shorter.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftFile = Path.GetFileName(left);
        var rightFile = Path.GetFileName(right);
        if (!string.Equals(leftFile, rightFile, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(leftFile))
        {
            return false;
        }

        var leftParent = Path.GetFileName(Path.GetDirectoryName(left.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty);
        var rightParent = Path.GetFileName(Path.GetDirectoryName(right.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty);
        return !string.IsNullOrWhiteSpace(leftParent)
               && string.Equals(leftParent, rightParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMediaServerPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimEnd('/');
    }

    private async Task<List<NavidromeAudioTrack>> SearchNavidromeTrackCandidatesAsync(
        NavidromeConnection navidrome,
        SyncTrackSummary track,
        string primaryQuery,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        var candidates = new List<NavidromeAudioTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in BuildServerSearchQueries(track, primaryQuery))
        {
            if (cache.TryGetValue(query, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                candidates.Add(new NavidromeAudioTrack(cached, track.Name, track.Artists, track.DurationMs));
                continue;
            }

            var results = await _navidromeApiClient.SearchTracksAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                query,
                cancellationToken);
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Id) && seen.Add(result.Id))
                {
                    candidates.Add(result);
                }
            }

            if (results.Count == 0)
            {
                cache[query] = null;
            }
        }

        return candidates;
    }

    private static IEnumerable<string> BuildServerSearchQueries(SyncTrackSummary track, string primaryQuery)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in new[]
                 {
                     primaryQuery,
                     track.Name,
                     $"{track.Artists} {track.Name}".Trim()
                 })
        {
            var normalized = (query ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static PlaylistSyncResult BuildCompletedResult(
        string message,
        string? playlistId,
        SyncMatchSummary matchSummary,
        int syncedTracks)
        => PlaylistSyncResult.Completed(
            message,
            playlistId,
            syncedTracks,
            matchSummary.SourceTracks,
            matchSummary.LocalMatches,
            matchSummary.TargetMatches,
            matchSummary.MissingTracks,
            matchSummary.MetadataMatches,
            matchSummary.SearchMatches);

    private static PlaylistSyncResult BuildIdentityGapResult(
        string message,
        string? playlistId,
        SyncMatchSummary matchSummary,
        int syncedTracks)
        => PlaylistSyncResult.IdentityGap(
            message,
            playlistId,
            syncedTracks,
            matchSummary.SourceTracks,
            matchSummary.LocalMatches,
            matchSummary.TargetMatches,
            matchSummary.MissingTracks,
            matchSummary.MetadataMatches,
            matchSummary.SearchMatches);

    private static PlaylistSyncResult BuildWriteFailureResult(
        string message,
        SyncMatchSummary matchSummary,
        string? playlistId = null,
        int syncedTracks = 0)
        => PlaylistSyncResult.Failed(
            message,
            PlaylistSyncResult.ClassifyKind(message),
            playlistId,
            syncedTracks,
            matchSummary.SourceTracks,
            matchSummary.LocalMatches,
            matchSummary.TargetMatches,
            matchSummary.MissingTracks,
            matchSummary.MetadataMatches,
            matchSummary.SearchMatches);

    private static string BuildSyncMessage(string baseMessage, SyncMatchSummary matchSummary)
    {
        return string.Concat(
            baseMessage,
            " Source tracks: ",
            matchSummary.SourceTracks.ToString(CultureInfo.InvariantCulture),
            ". Local matches: ",
            matchSummary.LocalMatches.ToString(CultureInfo.InvariantCulture),
            ". Target matches: ",
            matchSummary.TargetMatches.ToString(CultureInfo.InvariantCulture),
            ". Missing tracks: ",
            matchSummary.MissingTracks.ToString(CultureInfo.InvariantCulture),
            ".");
    }

    private static bool IsTitleArtistMatch(SyncTrackSummary track, PlexTrack result)
        => TrackTitleMatcher.TitlesMatch(track.Name, result.Title)
           && TrackTitleMatcher.ArtistsMatch(track.Artists, result.Artist);

    private static bool IsTitleArtistMatch(SyncTrackSummary track, JellyfinAudioTrack result)
        => TrackTitleMatcher.TitlesMatch(track.Name, result.Name)
           && TrackTitleMatcher.ArtistsMatch(track.Artists, result.Artist);

    private static bool IsTitleArtistMatch(SyncTrackSummary track, NavidromeAudioTrack result)
        => TrackTitleMatcher.TitlesMatch(track.Name, result.Title)
           && TrackTitleMatcher.ArtistsMatch(track.Artists, result.Artist);

    private static bool IsDurationMatch(int? durationMs, long durationCandidate)
    {
        if (!durationMs.HasValue || durationCandidate <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - durationCandidate);
        return delta <= DurationToleranceMs;
    }

    private static bool IsDurationMatch(int? durationMs, int? durationCandidateMs)
    {
        if (!durationMs.HasValue || !durationCandidateMs.HasValue || durationCandidateMs <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - durationCandidateMs.Value);
        return delta <= DurationToleranceMs;
    }

    private static string BuildMergeTrackDedupKey(SyncTrackSummary track)
    {
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            return $"isrc:{Normalize(track.Isrc)}";
        }

        var year = TryParseReleaseYear(track.ReleaseDate, out var parsedYear)
            ? parsedYear.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var durationBucket = track.DurationMs.HasValue && track.DurationMs.Value > 0
            ? (track.DurationMs.Value / DurationToleranceMs).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var sourceTrackKey = string.IsNullOrWhiteSpace(track.SourceTrackId)
            ? string.Empty
            : Normalize(track.SourceTrackId);
        return string.Join(
            "\u001F",
            Normalize(track.Name),
            Normalize(track.Artists),
            Normalize(track.Album),
            year,
            durationBucket,
            sourceTrackKey);
    }

    private static string ResolveMergedPlaylistName(string? requestedName)
    {
        var trimmed = (requestedName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "Merged Monitored Playlist" : trimmed;
    }

    private static string? BuildMergedPlaylistDescription(
        string? userDescription,
        IEnumerable<PlaylistWatchlistDto> selectedPlaylists,
        string? sourceUsername)
    {
        var values = new List<string>();
        var trimmedDescription = (userDescription ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedDescription))
        {
            values.Add(trimmedDescription);
        }

        var sourceSummary = BuildMergeSourceSummary(selectedPlaylists);
        if (!string.IsNullOrWhiteSpace(sourceSummary))
        {
            values.Add(sourceSummary);
        }

        var trimmedUser = (sourceUsername ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedUser))
        {
            values.Add($"Source user: {trimmedUser}");
        }

        return values.Count == 0 ? null : string.Join(" | ", values);
    }

    private static string? BuildMergeSourceSummary(IEnumerable<PlaylistWatchlistDto> selectedPlaylists)
    {
        var sources = selectedPlaylists
            .Select(static playlist => NormalizeMergeSourceLabel(playlist.Source))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return sources.Count == 0 ? null : $"Sources: {string.Join(", ", sources)}";
    }

    private static string NormalizeMergeSourceLabel(string? source)
    {
        var normalized = NormalizeSource(source);
        return normalized switch
        {
            "spotify" => "Spotify",
            "deezer" => "Deezer",
            "apple" => "Apple Music",
            "boomplay" => "Boomplay",
            "recommendations" => "Recommendations",
            "smarttracklist" => "Smart Tracklist",
            _ => string.IsNullOrWhiteSpace(normalized)
                ? "Unknown"
                : char.ToUpperInvariant(normalized[0]) + normalized[1..]
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static SyncTrackSummary ToSyncTrackSummary(SpotifyTrackSummary track)
    {
        return new SyncTrackSummary(
            (track.Id ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
            track.Name?.Trim() ?? string.Empty,
            track.Artists?.Trim() ?? string.Empty,
            track.Album?.Trim() ?? string.Empty,
            track.ReleaseDate,
            track.Explicit,
            NormalizeGenres(track.Genres),
            track.DurationMs);
    }

    private static SyncTrackSummary ToSyncTrackSummary(PlaylistTrackCandidate track)
    {
        return new SyncTrackSummary(
            (track.TrackSourceId ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
            track.Title?.Trim() ?? string.Empty,
            track.Artist?.Trim() ?? string.Empty,
            track.Album?.Trim() ?? string.Empty,
            track.ReleaseYear?.ToString(CultureInfo.InvariantCulture),
            track.Explicit,
            NormalizeGenres(track.Genres),
            track.DurationMs,
            string.IsNullOrWhiteSpace(track.DeezerId) ? null : "deezer",
            string.IsNullOrWhiteSpace(track.DeezerId) ? null : track.DeezerId.Trim());
    }

    private static (string Source, string TrackId) ResolveTrackIdentity(
        string playlistSource,
        SyncTrackSummary track)
    {
        var source = string.IsNullOrWhiteSpace(track.IdentitySource)
            ? NormalizeSource(playlistSource)
            : NormalizeSource(track.IdentitySource);
        var trackId = string.IsNullOrWhiteSpace(track.IdentityTrackId)
            ? track.SourceTrackId
            : track.IdentityTrackId.Trim();
        return (source, trackId);
    }

    private static IReadOnlyList<string> NormalizeGenres(IReadOnlyList<string>? genres)
    {
        if (genres is null || genres.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return genres
            .Select(genre => (genre ?? string.Empty).Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(seen.Add)
            .ToList();
    }
}

public enum PlaylistSyncResultKind
{
    Completed,
    IdentityGap,
    NoLocalTracks,
    WriteLag,
    Retry,
    Blocked
}

public sealed record PlaylistSyncResult(
    bool Success,
    string Message,
    string? PlaylistId = null,
    int SyncedTracks = 0,
    int SourceTracks = 0,
    int LocalMatches = 0,
    int TargetMatches = 0,
    int MissingTracks = 0,
    int MetadataMatches = 0,
    int SearchMatches = 0,
    PlaylistSyncResultKind Kind = PlaylistSyncResultKind.Completed)
{
    public static PlaylistSyncResult Failed(string message, PlaylistSyncResultKind kind)
        => Failed(message, kind, playlistId: null);

    public static PlaylistSyncResult Failed(
        string message,
        PlaylistSyncResultKind kind,
        string? playlistId,
        int syncedTracks = 0,
        int sourceTracks = 0,
        int localMatches = 0,
        int targetMatches = 0,
        int missingTracks = 0,
        int metadataMatches = 0,
        int searchMatches = 0)
    {
        if (kind is PlaylistSyncResultKind.Completed
            or PlaylistSyncResultKind.IdentityGap
            or PlaylistSyncResultKind.NoLocalTracks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Use Completed / IdentityGap / NoLocalTracks factories.");
        }

        return new(
            false,
            message,
            playlistId,
            syncedTracks,
            sourceTracks,
            localMatches,
            targetMatches,
            missingTracks,
            metadataMatches,
            searchMatches,
            kind);
    }

    public static PlaylistSyncResult Completed(
        string message,
        string? playlistId = null,
        int syncedTracks = 0,
        int sourceTracks = 0,
        int localMatches = 0,
        int targetMatches = 0,
        int missingTracks = 0,
        int metadataMatches = 0,
        int searchMatches = 0)
        => new(
            true,
            message,
            playlistId,
            syncedTracks,
            sourceTracks,
            localMatches,
            targetMatches,
            missingTracks,
            metadataMatches,
            searchMatches,
            PlaylistSyncResultKind.Completed);

    public static PlaylistSyncResult IdentityGap(
        string message,
        string? playlistId = null,
        int syncedTracks = 0,
        int sourceTracks = 0,
        int localMatches = 0,
        int targetMatches = 0,
        int missingTracks = 0,
        int metadataMatches = 0,
        int searchMatches = 0)
        => new(
            true,
            message,
            playlistId,
            syncedTracks,
            sourceTracks,
            localMatches,
            targetMatches,
            missingTracks,
            metadataMatches,
            searchMatches,
            PlaylistSyncResultKind.IdentityGap);

    public static PlaylistSyncResult NoLocalTracks(
        string message,
        string? playlistId = null,
        int syncedTracks = 0,
        int sourceTracks = 0,
        int localMatches = 0,
        int targetMatches = 0,
        int missingTracks = 0,
        int metadataMatches = 0,
        int searchMatches = 0)
        => new(
            true,
            message,
            playlistId,
            syncedTracks,
            sourceTracks,
            localMatches,
            targetMatches,
            missingTracks,
            metadataMatches,
            searchMatches,
            PlaylistSyncResultKind.NoLocalTracks);

    public static PlaylistSyncResult FailedFromMessage(string message)
        => Failed(message, ClassifyKind(message));

    internal static PlaylistSyncResultKind ClassifyKind(string message)
    {
        var text = message ?? string.Empty;
        if (IsBlockedConfigMessage(text))
        {
            return PlaylistSyncResultKind.Blocked;
        }

        if (IsSourceLoadMessage(text))
        {
            return PlaylistSyncResultKind.Retry;
        }

        if (ContainsOrdinalIgnoreCase(text, "verification is incomplete")
            || ContainsOrdinalIgnoreCase(text, "Source tracks:"))
        {
            return PlaylistSyncResultKind.WriteLag;
        }

        return PlaylistSyncResultKind.Retry;
    }

    internal static bool IsBlockedConfigMessage(string message)
        => string.Equals(message, "Playlist not available.", StringComparison.OrdinalIgnoreCase)
           || string.Equals(message, "No target server selected.", StringComparison.OrdinalIgnoreCase)
           || string.Equals(message, "Playlist sync target is disabled.", StringComparison.OrdinalIgnoreCase)
           || string.Equals(message, "Unsupported playlist sync target.", StringComparison.OrdinalIgnoreCase)
           || string.Equals(message, "No eligible tracks after blocked/ignored filtering.", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLibraryEmptyMessage(string message)
        => ContainsOrdinalIgnoreCase(message, "No eligible playlist tracks are visible in the DeezSpoTag library yet.");

    internal static bool IsNoTargetMatchesMessage(string message)
        => ContainsOrdinalIgnoreCase(message, "No Plex matches found for this playlist.")
           || ContainsOrdinalIgnoreCase(message, "No Jellyfin matches found for this playlist.")
           || ContainsOrdinalIgnoreCase(message, "No Navidrome matches found for this playlist.");

    internal static bool IsSourceLoadMessage(string message)
        => ContainsOrdinalIgnoreCase(message, "Spotify playlist could not be loaded.")
           || ContainsOrdinalIgnoreCase(message, "Track candidates are unavailable for this source");

    private static bool ContainsOrdinalIgnoreCase(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of provisioning (or verifying) one target's playlist container -- see
/// PlaylistSyncService.EnsureTargetPlaylistContainersAsync.</summary>
public sealed record PlaylistProvisioningOutcome(
    string TargetService,
    bool Created,
    string? PlaylistId,
    string Message);

public sealed record PlaylistMembershipDelta(
    IReadOnlyList<string> ToAdd,
    IReadOnlyList<string> ToRemove,
    bool NeedsReorder,
    IReadOnlyList<string> IntendedOrder);
