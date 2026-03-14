using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistSyncService
{
    private sealed record PlexConnection(string Url, string Token, string MachineIdentifier);
    private const int DurationToleranceMs = 2000;
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly ILogger<PlaylistSyncService> _logger;

    public PlaylistSyncService(
        LibraryRepository libraryRepository,
        SpotifyMetadataService spotifyMetadataService,
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        ILogger<PlaylistSyncService> logger)
    {
        _libraryRepository = libraryRepository;
        _spotifyMetadataService = spotifyMetadataService;
        _plexApiClient = plexApiClient;
        _authService = authService;
        _logger = logger;
    }

    public async Task<PlaylistSyncResult> SyncSpotifyPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        bool force,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return new PlaylistSyncResult(false, "Playlist not available.");
        }

        var service = preference?.Service?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistSyncResult(false, "No target server selected.");
        }

        if (service == "jellyfin")
        {
            _logger.LogInformation("Jellyfin playlist sync is not wired yet for {SourceId}.", playlist.SourceId);
            return new PlaylistSyncResult(false, "Jellyfin sync not wired yet.");
        }

        if (service != "plex")
        {
            return new PlaylistSyncResult(false, "Unsupported playlist sync target.");
        }

        return await SyncSpotifyToPlexAsync(playlist, preference, cancellationToken);
    }

    private async Task<PlaylistSyncResult> SyncSpotifyToPlexAsync(
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
            return new PlaylistSyncResult(false, "Plex is not configured.");
        }

        var snapshot = await _spotifyMetadataService.FetchPlaylistSnapshotAsync(playlist.SourceId, cancellationToken);
        var tracks = snapshot?.Tracks ?? new List<SpotifyTrackSummary>();
        if (snapshot == null)
        {
            return new PlaylistSyncResult(false, "Spotify playlist could not be loaded.");
        }

        if (tracks.Count == 0)
        {
            return new PlaylistSyncResult(false, "Spotify playlist has no tracks.");
        }

        var playlistName = snapshot.Name ?? playlist.Name ?? "Playlist";
        var description = snapshot.Description ?? playlist.Description;
        var imageUrl = snapshot.ImageUrl ?? playlist.ImageUrl;
        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(tracks, cancellationToken);
        var ratingKeys = await ResolvePlexRatingKeysAsync(plex, tracks, orderedTrackIds, cancellationToken);

        if (ratingKeys.Count == 0)
        {
            _logger.LogWarning("No Plex matches found for Spotify playlist {PlaylistId}.", playlist.SourceId);
            return new PlaylistSyncResult(false, "No Plex matches found for this playlist.");
        }

        var playlistId = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            playlistName,
            ratingKeys,
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return new PlaylistSyncResult(false, "Failed to create Plex playlist.");
        }

        await _plexApiClient.UpdatePlaylistMetadataAsync(
            plex.Url,
            plex.Token,
            playlistId,
            playlistName,
            description,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(imageUrl) && preference?.UpdateArtwork != false)
        {
            await _plexApiClient.UpdatePlaylistPosterFromUrlAsync(
                plex.Url,
                plex.Token,
                playlistId,
                imageUrl,
                cancellationToken);
        }

        return new PlaylistSyncResult(true, "Playlist synced.", playlistId, ratingKeys.Count);
    }

    private async Task<(PlexConnection? Plex, PlaylistSyncResult? Error)> TryLoadConfiguredPlexAsync()
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return (null, new PlaylistSyncResult(false, "Plex is not configured."));
        }

        if (string.IsNullOrWhiteSpace(plex.MachineIdentifier))
        {
            return (null, new PlaylistSyncResult(false, "Plex machine identifier missing."));
        }

        return (new PlexConnection(plex.Url, plex.Token, plex.MachineIdentifier), null);
    }

    private async Task<List<long>> ResolveOrderedTrackIdsAsync(
        IReadOnlyList<SpotifyTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var isrcs = tracks
            .Select(track => track.Isrc)
            .Where(isrc => !string.IsNullOrWhiteSpace(isrc))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var trackIdByIsrc = await _libraryRepository.GetTrackIdsBySourceIdsAsync("isrc", isrcs, cancellationToken);
        return tracks
            .Select(track => !string.IsNullOrWhiteSpace(track.Isrc)
                && trackIdByIsrc.TryGetValue(track.Isrc, out var trackId)
                    ? trackId
                    : 0L)
            .ToList();
    }

    private async Task<List<string>> ResolvePlexRatingKeysAsync(
        PlexConnection plex,
        IReadOnlyList<SpotifyTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var ratingKeyByTrackId = await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(
            orderedTrackIds.Where(id => id > 0).Distinct().ToList(),
            cancellationToken);

        var ratingKeys = new List<string>(tracks.Count);
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var trackId = orderedTrackIds[i];
            if (trackId > 0 && ratingKeyByTrackId.TryGetValue(trackId, out var ratingKey))
            {
                ratingKeys.Add(ratingKey);
                continue;
            }

            var resolved = await ResolvePlexRatingKeyAsync(plex, track, searchCache, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                ratingKeys.Add(resolved);
            }
        }

        return ratingKeys;
    }

    private async Task<string?> ResolvePlexRatingKeyAsync(
        PlexConnection plex,
        SpotifyTrackSummary track,
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

        var match = results.FirstOrDefault(result =>
            IsTitleArtistMatch(track, result)
            && IsDurationMatch(track.DurationMs, result.DurationMs));

        var ratingKey = match?.RatingKey;
        cache[query] = ratingKey;
        return ratingKey;
    }

    private static bool IsTitleArtistMatch(SpotifyTrackSummary track, PlexTrack result)
    {
        var leftTitle = Normalize(track.Name);
        var rightTitle = Normalize(result.Title);
        var leftArtist = Normalize(track.Artists ?? "");
        var rightArtist = Normalize(result.Artist);
        return leftTitle == rightTitle && leftArtist == rightArtist;
    }

    private static bool IsDurationMatch(int? durationMs, long durationCandidate)
    {
        if (!durationMs.HasValue || durationCandidate <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - durationCandidate);
        return delta <= DurationToleranceMs;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }
}

public sealed record PlaylistSyncResult(
    bool Success,
    string Message,
    string? PlaylistId = null,
    int SyncedTracks = 0);
