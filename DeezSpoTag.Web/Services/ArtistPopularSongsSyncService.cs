using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistPopularSongsSyncService
{
    private const string SpotifySource = "spotify";
    private const string PlexTarget = "plex";
    private const string JellyfinTarget = "jellyfin";
    private const string NavidromeTarget = "navidrome";
    private const string BothTargets = "both";
    private const string SyncModeMirror = "mirror";
    private const string ArtistTopSourcePrefix = "artist-top:";

    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly LibraryConfigStore _configStore;
    private readonly ILogger<ArtistPopularSongsSyncService> _logger;

    public ArtistPopularSongsSyncService(
        LibraryRepository libraryRepository,
        SpotifyArtistService spotifyArtistService,
        PlaylistSyncService playlistSyncService,
        LibraryConfigStore configStore,
        ILogger<ArtistPopularSongsSyncService> logger)
    {
        _libraryRepository = libraryRepository;
        _spotifyArtistService = spotifyArtistService;
        _playlistSyncService = playlistSyncService;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<ArtistPopularSongsSyncResult> SyncAsync(
        long artistId,
        string? target,
        CancellationToken cancellationToken)
        => await SyncAsync(artistId, ResolveTargets(target), cancellationToken);

    public async Task<ArtistPopularSongsSyncResult> SyncAsync(
        long artistId,
        IReadOnlyList<string> targets,
        CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return ArtistPopularSongsSyncResult.Failed("Artist not found.");
        }

        return await SyncAsync(artist.Id, artist.Name, targets, cancellationToken);
    }

    public async Task<ArtistPopularSongsSyncResult> SyncAsync(
        long artistId,
        string artistName,
        string? target,
        CancellationToken cancellationToken)
        => await SyncAsync(artistId, artistName, ResolveTargets(target), cancellationToken);

    public async Task<ArtistPopularSongsSyncResult> SyncAsync(
        long artistId,
        string artistName,
        IReadOnlyList<string> targets,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName))
        {
            return ArtistPopularSongsSyncResult.Failed("Artist not found.");
        }

        var artistPage = await _spotifyArtistService.GetArtistPageAsync(
            artistId,
            artistName,
            forceRefresh: false,
            forceRematch: false,
            cancellationToken,
            includeDeezerLinking: true);
        if (artistPage?.Artist is null || string.IsNullOrWhiteSpace(artistPage.Artist.Id))
        {
            return ArtistPopularSongsSyncResult.Failed("Spotify artist top songs are unavailable.");
        }

        var candidates = BuildTrackCandidates(artistPage);
        if (candidates.Count == 0)
        {
            return ArtistPopularSongsSyncResult.Failed("Spotify artist top songs are unavailable.");
        }

        var playlist = BuildPlaylist(artistPage, candidates.Count);
        var targetServices = NormalizeTargets(targets);
        var results = new List<ArtistPopularSongsTargetResult>(targetServices.Count);
        foreach (var service in targetServices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preference = BuildPreference(playlist, service);
            var syncResult = await _playlistSyncService.SyncAvailablePlaylistTracksAsync(
                playlist,
                preference,
                candidates,
                force: true,
                cancellationToken);
            results.Add(new ArtistPopularSongsTargetResult(
                service,
                syncResult.Success,
                syncResult.Message,
                syncResult.SyncedTracks,
                syncResult.MissingTracks));
        }

        var success = results.Any(result => result.Success);
        var message = BuildResultMessage(artistPage.Artist.Name, results);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            success ? "info" : "warn",
            message));
        if (!success && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Artist popular songs sync failed for {ArtistId} {ArtistName}: {Message}",
                artistId,
                artistName,
                message);
        }

        return new ArtistPopularSongsSyncResult(success, message, results);
    }

    private static PlaylistWatchlistDto BuildPlaylist(SpotifyArtistPageResult artistPage, int trackCount)
    {
        var spotifyId = artistPage.Artist.Id.Trim();
        return new PlaylistWatchlistDto(
            Id: 0,
            Source: SpotifySource,
            SourceId: $"{ArtistTopSourcePrefix}{spotifyId}",
            Name: $"{artistPage.Artist.Name} - Popular Songs",
            ImageUrl: ResolveArtistImageUrl(artistPage.Artist),
            Description: $"Popular songs for {artistPage.Artist.Name}, synced by DeezSpoTag.",
            TrackCount: trackCount,
            CreatedAt: DateTimeOffset.UtcNow,
            OwnerName: "DeezSpoTag");
    }

    private static PlaylistWatchPreferenceDto BuildPreference(PlaylistWatchlistDto playlist, string service)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlaylistWatchPreferenceDto(
            Source: playlist.Source,
            SourceId: playlist.SourceId,
            DestinationFolderId: null,
            Service: service,
            PreferredEngine: null,
            DownloadEngineOrder: null,
            DownloadVariantMode: null,
            SyncMode: SyncModeMirror,
            UpdateArtwork: true,
            ReuseSavedArtwork: false,
            CreatedAt: now,
            UpdatedAt: now);
    }

    private static List<PlaylistWatchService.PlaylistTrackCandidate> BuildTrackCandidates(SpotifyArtistPageResult artistPage)
    {
        var candidates = new List<PlaylistWatchService.PlaylistTrackCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in artistPage.TopTracks)
        {
            var spotifyTrackId = (track.Id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(spotifyTrackId) || !seen.Add(spotifyTrackId))
            {
                continue;
            }

            candidates.Add(new PlaylistWatchService.PlaylistTrackCandidate(
                spotifyTrackId,
                string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                track.Name?.Trim() ?? string.Empty,
                ResolveTrackArtist(track, artistPage.Artist.Name),
                track.AlbumName?.Trim() ?? string.Empty,
                TryParseReleaseYear(track.ReleaseDate, out var year) ? year : null,
                track.DurationMs > 0 ? track.DurationMs : null,
                track.Explicit,
                Array.Empty<string>(),
                ResolveTrackImageUrl(track)));
        }

        return candidates;
    }

    private static IReadOnlyList<string> ResolveTargets(string? target)
    {
        var normalized = (target ?? PlexTarget).Trim().ToLowerInvariant();
        return normalized switch
        {
            BothTargets => new[] { PlexTarget, JellyfinTarget },
            JellyfinTarget => new[] { JellyfinTarget },
            NavidromeTarget => new[] { NavidromeTarget },
            _ => new[] { PlexTarget }
        };
    }

    private static IReadOnlyList<string> NormalizeTargets(IReadOnlyList<string>? targets)
    {
        if (targets is null || targets.Count == 0)
        {
            return new[] { PlexTarget };
        }

        var normalized = targets
            .SelectMany(target => ResolveTargets(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? new[] { PlexTarget } : normalized;
    }

    private static string ResolveTrackArtist(SpotifyTrack track, string fallbackArtistName)
    {
        var artistName = track.ArtistName?.Trim();
        return string.IsNullOrWhiteSpace(artistName) ? fallbackArtistName.Trim() : artistName;
    }

    private static string? ResolveTrackImageUrl(SpotifyTrack track)
        => track.AlbumImages
            .Where(static image => !string.IsNullOrWhiteSpace(image.Url))
            .OrderByDescending(static image => image.Width ?? 0)
            .Select(static image => image.Url)
            .FirstOrDefault();

    private static string? ResolveArtistImageUrl(SpotifyArtistProfile artist)
        => artist.Images
            .Where(static image => !string.IsNullOrWhiteSpace(image.Url))
            .OrderByDescending(static image => image.Width ?? 0)
            .Select(static image => image.Url)
            .FirstOrDefault();

    private static bool TryParseReleaseYear(string? releaseDate, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(releaseDate) || releaseDate.Length < 4)
        {
            return false;
        }

        return int.TryParse(releaseDate.AsSpan(0, 4), out year);
    }

    private static string BuildResultMessage(
        string artistName,
        IReadOnlyList<ArtistPopularSongsTargetResult> results)
    {
        var summary = string.Join(
            "; ",
            results.Select(result =>
                $"{result.Target}: {(result.Success ? "synced" : "failed")} ({result.SyncedTracks} synced, {result.MissingTracks} missing)"));
        return $"Popular songs sync for {artistName}: {summary}.";
    }
}

public sealed record ArtistPopularSongsSyncResult(
    bool Success,
    string Message,
    IReadOnlyList<ArtistPopularSongsTargetResult> Targets)
{
    public static ArtistPopularSongsSyncResult Failed(string message)
        => new(false, message, Array.Empty<ArtistPopularSongsTargetResult>());
}

public sealed record ArtistPopularSongsTargetResult(
    string Target,
    bool Success,
    string Message,
    int SyncedTracks,
    int MissingTracks);
