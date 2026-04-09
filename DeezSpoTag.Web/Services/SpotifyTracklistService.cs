using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyTracklistService
{
    private const int ImmediateResolveLimit = 40;
    private const string PlaylistType = "playlist";
    private enum SpotifyContentType
    {
        Playlist,
        Track,
        Album,
        Show,
        Episode
    }

    private static readonly TimeSpan SnapshotCacheTtl = TimeSpan.FromHours(2);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Stamp, SpotifyTracklistMatchSnapshot Snapshot)> SnapshotCache = new();
    private static readonly TimeSpan DeezerSnapshotTtl = TimeSpan.FromHours(2);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeezerSnapshotCacheEntry> DeezerSnapshotCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _playlistMatchPreparationInFlight = new(StringComparer.Ordinal);
    private readonly SpotifyMetadataService _metadataService;
    private readonly DeezerClient _deezerClient;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly ISpotifyTracklistMatchQueue _matchQueue;
    private readonly ISpotifyTracklistMatchStore _matchStore;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SpotifyTracklistService> _logger;

    public SpotifyTracklistService(
        SpotifyMetadataService metadataService,
        DeezerClient deezerClient,
        SongLinkResolver songLinkResolver,
        ISpotifyTracklistMatchQueue matchQueue,
        ISpotifyTracklistMatchStore matchStore,
        ISettingsService settingsService,
        ILogger<SpotifyTracklistService> logger)
    {
        _metadataService = metadataService;
        _deezerClient = deezerClient;
        _songLinkResolver = songLinkResolver;
        _matchQueue = matchQueue;
        _matchStore = matchStore;
        _settingsService = settingsService;
        _logger = logger;
    }

    public Task<SpotifyTracklistPayload?> GetPlaylistTracklistAsync(string url, CancellationToken cancellationToken)
    {
        return GetTracklistAsync(url, cancellationToken);
    }

    public SpotifyTracklistMatchStart? StartPlaylistMatching(string url)
    {
        if (!SpotifyMetadataService.TryParseSpotifyUrl(url, out var type, out var playlistId)
            || !string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = BuildMatchToken(PlaylistType, playlistId);
        var existing = _matchStore.GetSnapshot(token);
        if (existing is { Pending: > 0 })
        {
            return new SpotifyTracklistMatchStart(token, existing.Pending);
        }

        _matchStore.Activate(token);
        _matchStore.Start(token, pendingCount: 1, signature: null);
        QueuePlaylistMatchPreparation(token, playlistId);
        return new SpotifyTracklistMatchStart(token, 1);
    }

    public async Task<SpotifyTracklistPayload?> GetTracklistAsync(string url, CancellationToken cancellationToken)
    {
        var metadata = await _metadataService.FetchByUrlAsync(url, cancellationToken);
        if (metadata == null)
        {
            return null;
        }

        if (!TryParseContentType(metadata.Type, out var contentType))
        {
            return null;
        }

        var settings = _settingsService.LoadSettings();
        var (resolvedMetadata, tracks) = await ResolveTracksForContentAsync(metadata, contentType, settings, cancellationToken);
        var type = resolvedMetadata.Type ?? string.Empty;
        var token = BuildMatchToken(type, resolvedMetadata.Id);
        var signature = contentType == SpotifyContentType.Playlist
            ? BuildPlaylistSignature(tracks)
            : string.Empty;
        var allowFallbackSearch = ShouldAllowFallbackSearch(contentType, settings);
        var conversion = await BuildConversionForContentAsync(
            contentType,
            token,
            signature,
            tracks,
            allowFallbackSearch,
            cancellationToken);

        StartOrSeedMatches(token, signature, conversion, allowFallbackSearch);

        var result = new SpotifyTracklistResult
        {
            Id = resolvedMetadata.Id,
            Title = resolvedMetadata.Name,
            Description = resolvedMetadata.Subtitle ?? string.Empty,
            Creator = new SpotifyTracklistCreator
            {
                Name = resolvedMetadata.OwnerName ?? "Spotify",
                Avatar = resolvedMetadata.OwnerImageUrl ?? string.Empty
            },
            Followers = resolvedMetadata.Followers,
            PictureXl = resolvedMetadata.ImageUrl ?? string.Empty,
            PictureBig = resolvedMetadata.ImageUrl ?? string.Empty,
            NbTracks = resolvedMetadata.TotalTracks ?? conversion.Tracks.Count,
            Tracks = conversion.Tracks
        };

        return new SpotifyTracklistPayload(result, token, conversion.Pending.Count);
    }

    public async Task<SpotifyTracklistBuildResult> BuildMatchedTracksAsync(
        string? type,
        string? id,
        List<SpotifyTrackSummary> tracks,
        bool allowFallbackSearch,
        CancellationToken cancellationToken)
    {
        var token = BuildMatchToken(type, id);
        if (tracks.Count == 0)
        {
            return new SpotifyTracklistBuildResult(
                token,
                0,
                new List<SpotifyTracklistTrack>());
        }

        var conversion = await ConvertTracksAsync(tracks, allowFallbackSearch, cancellationToken);
        conversion = ApplyStoredMatches(token, conversion);

        if (conversion.Pending.Count > 0)
        {
            _matchStore.Activate(token);
            _matchStore.Start(token, conversion.Pending.Count, signature: null);
            foreach (var pending in conversion.Pending)
            {
                _matchQueue.Enqueue(token, pending.Index, pending.Track, allowFallbackSearch);
            }
        }

        return new SpotifyTracklistBuildResult(token, conversion.Pending.Count, conversion.Tracks);
    }

    public SpotifyTracklistMatchStart? StartVisibleTrackMatching(
        string token,
        int offset,
        IReadOnlyList<SpotifyTrackSummary> tracks,
        bool allowFallbackSearch)
    {
        if (tracks.Count == 0)
        {
            return null;
        }

        _matchStore.Activate(token);
        _matchStore.Start(token, tracks.Count, signature: null);
        for (var i = 0; i < tracks.Count; i++)
        {
            _matchQueue.Enqueue(
                token,
                offset + i,
                tracks[i],
                allowFallbackSearch);
        }

        return new SpotifyTracklistMatchStart(token, tracks.Count);
    }

    private void StartOrSeedMatches(
        string token,
        string signature,
        SpotifyTracklistConversion conversion,
        bool allowFallbackSearch)
    {
        if (conversion.Pending.Count > 0)
        {
            _matchStore.Activate(token);
            _matchStore.Start(token, conversion.Pending.Count, signature);
            foreach (var pending in conversion.Pending)
            {
                _matchQueue.Enqueue(token, pending.Index, pending.Track, allowFallbackSearch);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(signature))
        {
            return;
        }

        _matchStore.Activate(token);
        _matchStore.Start(token, pendingCount: 0, signature);

        var entries = conversion.Tracks
            .Select(track => new SpotifyTracklistMatchEntry(
                track.Index,
                track.Id,
                ExtractSpotifyIdFromTrack(track),
                "matched",
                "snapshot_seed",
                1))
            .ToList();
        _matchStore.CacheSignatureSnapshot(signature, entries);
    }

    private void QueuePlaylistMatchPreparation(string token, string playlistId)
    {
        if (!_playlistMatchPreparationInFlight.TryAdd(token, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PreparePlaylistMatchingAsync(token, playlistId);
            }
            finally
            {
                _playlistMatchPreparationInFlight.TryRemove(token, out _);
            }
        }, CancellationToken.None);
    }

    private async Task PreparePlaylistMatchingAsync(string token, string playlistId)
    {
        try
        {
            var metadata = await _metadataService.FetchPlaylistMetadataAsync(playlistId, CancellationToken.None);
            if (metadata is null)
            {
                _matchStore.Start(token, pendingCount: 0, signature: null);
                return;
            }

            var settings = _settingsService.LoadSettings();
            var tracks = await _metadataService.FetchPlaylistTracksForSourceAsync(
                playlistId,
                settings.SpotifyPlaylistTrackSource,
                CancellationToken.None);
            if (tracks.Count == 0)
            {
                _matchStore.Start(token, pendingCount: 0, signature: null);
                return;
            }

            tracks = await _metadataService.HydrateTrackIsrcsWithPathfinderAsync(
                tracks,
                CancellationToken.None,
                settings.SpotifyIsrcHydrationConcurrency);
            if (tracks.Any(t => string.IsNullOrWhiteSpace(t.Name)))
            {
                tracks = await _metadataService.HydrateTrackDetailsWithBlobAsync(tracks, CancellationToken.None);
            }

            var signature = BuildPlaylistSignature(tracks);
            var strictSpotifyDeezerMode = settings.StrictSpotifyDeezerMode;
            var allowFallbackSearch = !strictSpotifyDeezerMode
                && (settings.FallbackSearch
                    || string.Equals(settings.SpotifyPlaylistTrackSource, "librespot", StringComparison.OrdinalIgnoreCase));
            if (!strictSpotifyDeezerMode && IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource))
            {
                allowFallbackSearch = true;
            }

            var conversion = new SpotifyTracklistConversion(
                SpotifyTracklistMapper.MapTracks(tracks, 0),
                tracks.Select((track, index) => new SpotifyTracklistPending(index, track)).ToList());

            if (!string.IsNullOrWhiteSpace(signature))
            {
                conversion = ApplyStoredSnapshot(signature, conversion);
            }
            conversion = ApplyStoredMatches(token, conversion);
            StartOrSeedMatches(token, signature, conversion, allowFallbackSearch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to prepare Spotify playlist matching for token {Token}.", token);
            _matchStore.Start(token, pendingCount: 0, signature: null);
        }
    }

    private async Task<(SpotifyUrlMetadata Metadata, List<SpotifyTrackSummary> Tracks)> ResolveTracksForContentAsync(
        SpotifyUrlMetadata metadata,
        SpotifyContentType contentType,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var tracks = metadata.TrackList;
        if (contentType == SpotifyContentType.Playlist)
        {
            var playlistMetadata = await _metadataService.FetchPlaylistMetadataAsync(metadata.Id, cancellationToken);
            if (playlistMetadata != null)
            {
                metadata = playlistMetadata with { TrackList = metadata.TrackList };
            }

            tracks = await _metadataService.FetchPlaylistTracksForSourceAsync(
                metadata.Id,
                settings.SpotifyPlaylistTrackSource,
                cancellationToken);
            tracks = await _metadataService.HydrateTrackIsrcsWithPathfinderAsync(
                tracks,
                cancellationToken,
                settings.SpotifyIsrcHydrationConcurrency);
            if (tracks.Any(t => string.IsNullOrWhiteSpace(t.Name)))
            {
                tracks = await _metadataService.HydrateTrackDetailsWithBlobAsync(tracks, cancellationToken);
            }
        }
        else if (contentType == SpotifyContentType.Album && tracks.Count == 0)
        {
            tracks = await _metadataService.FetchAlbumTracksAsync(metadata.Id, cancellationToken);
        }

        return (metadata, tracks);
    }

    private static bool ShouldAllowFallbackSearch(SpotifyContentType contentType, DeezSpoTagSettings settings)
    {
        if (settings.StrictSpotifyDeezerMode)
        {
            return false;
        }

        if (contentType == SpotifyContentType.Playlist)
        {
            return settings.FallbackSearch
                || string.Equals(settings.SpotifyPlaylistTrackSource, "librespot", StringComparison.OrdinalIgnoreCase)
                || IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource);
        }

        if (contentType is SpotifyContentType.Album or SpotifyContentType.Track)
        {
            return true;
        }

        return settings.FallbackSearch;
    }

    private async Task<SpotifyTracklistConversion> BuildConversionForContentAsync(
        SpotifyContentType contentType,
        string token,
        string signature,
        List<SpotifyTrackSummary> tracks,
        bool allowFallbackSearch,
        CancellationToken cancellationToken)
    {
        if (contentType is SpotifyContentType.Show or SpotifyContentType.Episode)
        {
            var mapped = SpotifyTracklistMapper.MapTracks(tracks, 0);
            return new SpotifyTracklistConversion(mapped, new List<SpotifyTracklistPending>());
        }

        var conversion = await ConvertTracksAsync(tracks, allowFallbackSearch, cancellationToken);
        if (contentType == SpotifyContentType.Playlist && !string.IsNullOrWhiteSpace(signature))
        {
            conversion = ApplyStoredSnapshot(signature, conversion);
        }

        return ApplyStoredMatches(token, conversion);
    }

    private static bool TryParseContentType(string? value, out SpotifyContentType contentType)
    {
        if (string.Equals(value, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            contentType = SpotifyContentType.Playlist;
            return true;
        }

        if (string.Equals(value, "track", StringComparison.OrdinalIgnoreCase))
        {
            contentType = SpotifyContentType.Track;
            return true;
        }

        if (string.Equals(value, "album", StringComparison.OrdinalIgnoreCase))
        {
            contentType = SpotifyContentType.Album;
            return true;
        }

        if (string.Equals(value, "show", StringComparison.OrdinalIgnoreCase))
        {
            contentType = SpotifyContentType.Show;
            return true;
        }

        if (string.Equals(value, "episode", StringComparison.OrdinalIgnoreCase))
        {
            contentType = SpotifyContentType.Episode;
            return true;
        }

        contentType = default;
        return false;
    }

    public async Task<List<SpotifyTracklistTrack>> ResolveVisibleTracksAsync(
        IReadOnlyList<SpotifyTrackSummary> tracks,
        int offset,
        string? snapshotId,
        bool allowFallbackSearch,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return new List<SpotifyTracklistTrack>();
        }

        var settings = _settingsService.LoadSettings();
        var resolveConcurrency = settings.SpotifyResolveConcurrency > 0
            ? settings.SpotifyResolveConcurrency
            : 1;
        var strictSpotifyDeezerMode = settings.StrictSpotifyDeezerMode;
        using var gate = new SemaphoreSlim(resolveConcurrency, resolveConcurrency);
        var resolved = new SpotifyTracklistTrack[tracks.Count];
        var tasks = new List<Task>(tracks.Count);
        var context = new VisibleResolveContext(
            offset,
            snapshotId,
            allowFallbackSearch,
            strictSpotifyDeezerMode);

        for (var i = 0; i < tracks.Count; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    resolved[index] = await ResolveVisibleTrackAsync(tracks[index], index, context, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        return resolved.ToList();
    }

    private async Task<SpotifyTracklistTrack> ResolveVisibleTrackAsync(
        SpotifyTrackSummary track,
        int index,
        VisibleResolveContext context,
        CancellationToken cancellationToken)
    {
        var spotifyId = ExtractSpotifyTrackId(track);
        var deezerId = string.Empty;
        if (!string.IsNullOrWhiteSpace(context.SnapshotId))
        {
            TryGetCachedDeezerId(context.SnapshotId, spotifyId, out deezerId);
        }

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            deezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                _deezerClient,
                _songLinkResolver,
                track,
                new SpotifyTrackResolveOptions(
                    AllowFallbackSearch: context.AllowFallbackSearch,
                    PreferIsrcOnly: !context.AllowFallbackSearch,
                    UseSongLink: context.AllowFallbackSearch,
                    StrictMode: context.StrictSpotifyDeezerMode,
                    BypassNegativeCanonicalCache: true,
                    Logger: _logger,
                    CancellationToken: cancellationToken)) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(context.SnapshotId) && !string.IsNullOrWhiteSpace(deezerId))
            {
                CacheDeezerId(context.SnapshotId, spotifyId, deezerId);
            }
        }

        var normalizedDeezerId = IsNumericDeezerId(deezerId) ? deezerId : string.Empty;
        var resolvedId = !string.IsNullOrWhiteSpace(normalizedDeezerId)
            ? normalizedDeezerId
            : spotifyId;
        var preview = string.IsNullOrWhiteSpace(normalizedDeezerId)
            ? string.Empty
            : $"/api/deezer/stream/{normalizedDeezerId}";
        return CreateTracklistTrackFromSummary(track, context.Offset + index, resolvedId, preview);
    }

    public List<SpotifyTracklistTrack> ApplyStoredMatchesToTracks(
        string token,
        List<SpotifyTracklistTrack> tracks)
    {
        if (string.IsNullOrWhiteSpace(token) || tracks.Count == 0)
        {
            return tracks;
        }

        var snapshot = _matchStore.GetSnapshot(token);
        if (snapshot == null || snapshot.Matches.Count == 0)
        {
            return tracks;
        }

        var matchesByIndex = snapshot.Matches
            .Where(entry => IsNumericDeezerId(entry.DeezerId))
            .ToDictionary(entry => entry.Index, entry => entry.DeezerId);
        var matchesBySpotifyId = snapshot.Matches
            .Where(entry => IsNumericDeezerId(entry.DeezerId) && !string.IsNullOrWhiteSpace(entry.SpotifyId))
            .GroupBy(entry => entry.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DeezerId, StringComparer.OrdinalIgnoreCase);
        if (matchesByIndex.Count == 0 && matchesBySpotifyId.Count == 0)
        {
            return tracks;
        }

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (IsNumericDeezerId(track.Id))
            {
                continue;
            }

            var resolvedDeezerId = TryResolveMatchedDeezerId(track, matchesByIndex, matchesBySpotifyId);
            if (!string.IsNullOrWhiteSpace(resolvedDeezerId))
            {
                tracks[i] = CloneTrackWithDeezerId(track, resolvedDeezerId);
            }
        }

        return tracks;
    }

    public async Task WarmVisibleTrackMatchesAsync(
        IReadOnlyList<SpotifyTrackSummary> tracks,
        int offset,
        string? snapshotId,
        bool allowFallbackSearch,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var settings = _settingsService.LoadSettings();
        var resolveConcurrency = settings.SpotifyResolveConcurrency > 0
            ? settings.SpotifyResolveConcurrency
            : 1;
        var strictSpotifyDeezerMode = settings.StrictSpotifyDeezerMode;
        var effectiveAllowFallbackSearch = !strictSpotifyDeezerMode && allowFallbackSearch;
        using var gate = new SemaphoreSlim(resolveConcurrency, resolveConcurrency);
        var tasks = new List<Task>(tracks.Count);

        for (var i = 0; i < tracks.Count; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var track = tracks[index];
                    if (!string.IsNullOrWhiteSpace(snapshotId)
                        && TryGetCachedDeezerId(snapshotId, track.Id, out var cached)
                        && !string.IsNullOrWhiteSpace(cached))
                    {
                        return;
                    }

                    var deezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                        _deezerClient,
                        _songLinkResolver,
                        track,
                        new SpotifyTrackResolveOptions(
                            AllowFallbackSearch: effectiveAllowFallbackSearch,
                            PreferIsrcOnly: !effectiveAllowFallbackSearch,
                            UseSongLink: effectiveAllowFallbackSearch,
                            StrictMode: strictSpotifyDeezerMode,
                            BypassNegativeCanonicalCache: true,
                            Logger: _logger,
                            CancellationToken: cancellationToken));

                    if (!string.IsNullOrWhiteSpace(snapshotId) && !string.IsNullOrWhiteSpace(deezerId))
                    {
                        CacheDeezerId(snapshotId, track.Id, deezerId);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private static bool TryGetCachedDeezerId(string snapshotId, string trackId, out string deezerId)
    {
        deezerId = string.Empty;
        if (string.IsNullOrWhiteSpace(snapshotId) || string.IsNullOrWhiteSpace(trackId))
        {
            return false;
        }

        if (!DeezerSnapshotCache.TryGetValue(snapshotId, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.Stamp > DeezerSnapshotTtl)
        {
            DeezerSnapshotCache.TryRemove(snapshotId, out _);
            return false;
        }

        if (entry.DeezerIds.TryGetValue(trackId, out var foundId) && !string.IsNullOrWhiteSpace(foundId))
        {
            deezerId = foundId;
            return true;
        }

        return false;
    }

    private static void CacheDeezerId(string snapshotId, string trackId, string deezerId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)
            || string.IsNullOrWhiteSpace(trackId)
            || string.IsNullOrWhiteSpace(deezerId))
        {
            return;
        }

        var entry = DeezerSnapshotCache.GetOrAdd(snapshotId, _ => new DeezerSnapshotCacheEntry());
        entry.Stamp = DateTimeOffset.UtcNow;
        entry.DeezerIds[trackId] = deezerId;
    }

    private sealed class DeezerSnapshotCacheEntry
    {
        public DateTimeOffset Stamp { get; set; } = DateTimeOffset.UtcNow;
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> DeezerIds { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private SpotifyTracklistConversion ApplyStoredMatches(string token, SpotifyTracklistConversion conversion)
    {
        var snapshot = _matchStore.GetSnapshot(token);
        if (snapshot == null || snapshot.Matches.Count == 0)
        {
            return conversion;
        }

        return ApplySnapshotMatches(conversion, snapshot);
    }

    private SpotifyTracklistConversion ApplyStoredSnapshot(string signature, SpotifyTracklistConversion conversion)
    {
        if (TryGetSnapshotFromCache(signature, out var cached))
        {
            return ApplySnapshotMatches(conversion, cached);
        }

        var snapshot = _matchStore.GetSnapshotBySignature(signature);
        if (snapshot == null || snapshot.Matches.Count == 0)
        {
            return conversion;
        }

        CacheSnapshot(signature, snapshot);
        return ApplySnapshotMatches(conversion, snapshot);
    }

    private static SpotifyTracklistConversion ApplySnapshotMatches(
        SpotifyTracklistConversion conversion,
        SpotifyTracklistMatchSnapshot snapshot)
    {
        var (matchesByIndex, matchesBySpotifyId) = BuildMatchLookups(snapshot.Matches);
        if (matchesByIndex.Count == 0 && matchesBySpotifyId.Count == 0)
        {
            return conversion;
        }

        var updatedTracks = new List<SpotifyTracklistTrack>(conversion.Tracks.Count);
        foreach (var track in conversion.Tracks)
        {
            if (IsNumericDeezerId(track.Id))
            {
                updatedTracks.Add(track);
                continue;
            }

            var resolvedDeezerId = TryResolveMatchedDeezerId(track, matchesByIndex, matchesBySpotifyId);
            if (!string.IsNullOrWhiteSpace(resolvedDeezerId))
            {
                updatedTracks.Add(CloneTrackWithDeezerId(track, resolvedDeezerId));
                continue;
            }

            updatedTracks.Add(track);
        }

        var updatedPending = conversion.Pending
            .Where(entry => IsPendingUnmatched(entry, matchesByIndex, matchesBySpotifyId))
            .ToList();

        return new SpotifyTracklistConversion(updatedTracks, updatedPending);
    }

    private static (Dictionary<int, string> ByIndex, Dictionary<string, string> BySpotifyId) BuildMatchLookups(
        IReadOnlyCollection<SpotifyTracklistMatchEntry> matches)
    {
        var byIndex = matches
            .Where(entry => IsNumericDeezerId(entry.DeezerId))
            .ToDictionary(entry => entry.Index, entry => entry.DeezerId);
        var bySpotifyId = matches
            .Where(entry => IsNumericDeezerId(entry.DeezerId) && !string.IsNullOrWhiteSpace(entry.SpotifyId))
            .GroupBy(entry => entry.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DeezerId, StringComparer.OrdinalIgnoreCase);
        return (byIndex, bySpotifyId);
    }

    private static bool IsPendingUnmatched(
        SpotifyTracklistPending pending,
        Dictionary<int, string> matchesByIndex,
        Dictionary<string, string> matchesBySpotifyId)
    {
        if (matchesByIndex.ContainsKey(pending.Index))
        {
            return false;
        }

        var spotifyId = ExtractSpotifyTrackId(pending.Track);
        return string.IsNullOrWhiteSpace(spotifyId) || !matchesBySpotifyId.ContainsKey(spotifyId);
    }

    private static string? TryResolveMatchedDeezerId(
        SpotifyTracklistTrack track,
        Dictionary<int, string> matchesByIndex,
        Dictionary<string, string> matchesBySpotifyId)
    {
        var spotifyId = ExtractSpotifyIdFromTrack(track);
        if (!string.IsNullOrWhiteSpace(spotifyId) && matchesBySpotifyId.TryGetValue(spotifyId, out var deezerId))
        {
            return deezerId;
        }

        return matchesByIndex.TryGetValue(track.Index, out var deezerIdByIndex)
            ? deezerIdByIndex
            : null;
    }

    private static bool TryGetSnapshotFromCache(string signature, out SpotifyTracklistMatchSnapshot snapshot)
    {
        snapshot = default!;
        if (!SnapshotCache.TryGetValue(signature, out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.Stamp > SnapshotCacheTtl)
        {
            SnapshotCache.TryRemove(signature, out _);
            return false;
        }

        snapshot = cached.Snapshot;
        return true;
    }

    private static void CacheSnapshot(string signature, SpotifyTracklistMatchSnapshot snapshot)
    {
        SnapshotCache[signature] = (DateTimeOffset.UtcNow, snapshot);
    }

    private static string BuildMatchToken(string? type, string? id)
    {
        return $"spotify:{type ?? PlaylistType}:{id ?? string.Empty}";
    }

    private static string BuildPlaylistSignature(List<SpotifyTrackSummary> tracks)
    {
        if (tracks.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder((tracks.Count * 24) + 8);
        builder.Append("sig-v2");
        for (var i = 0; i < tracks.Count; i++)
        {
            builder.Append('|');
            builder.Append(BuildTrackSignatureToken(tracks[i]));
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool IsPathfinderTrackSource(string? value)
    {
        return string.Equals(value, "pathfinder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "spotiflac", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ExtractSpotifyTrackId(SpotifyTrackSummary track)
    {
        var normalizedTrackId = TrackIdNormalization.NormalizeSpotifyTrackIdOrNull(track.Id);
        if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return normalizedTrackId;
        }

        return TrackIdNormalization.ExtractSpotifyTrackIdFromUrl(track.SourceUrl) ?? string.Empty;
    }

    private static string ExtractSpotifyIdFromTrack(SpotifyTracklistTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Id) && !IsNumericDeezerId(track.Id))
        {
            return track.Id;
        }

        return TrackIdNormalization.ExtractSpotifyTrackIdFromUrl(track.Link) ?? string.Empty;
    }

    private static string BuildTrackSignatureToken(SpotifyTrackSummary track)
    {
        var spotifyId = ExtractSpotifyTrackId(track);
        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            return spotifyId;
        }

        var title = (track.Name ?? string.Empty).Trim().ToLowerInvariant();
        var artist = (track.Artists ?? string.Empty).Trim().ToLowerInvariant();
        var duration = track.DurationMs.GetValueOrDefault();
        return $"{title}::{artist}::{duration}";
    }

    internal static SpotifyTracklistTrack CreateTracklistTrackFromSummary(
        SpotifyTrackSummary track,
        int index,
        string id,
        string preview)
    {
        var artistIds = CloneArtistIds(track.ArtistIds);
        return new SpotifyTracklistTrack
        {
            Id = id,
            Index = index,
            Title = track.Name ?? string.Empty,
            Isrc = track.Isrc ?? string.Empty,
            Duration = track.DurationMs.HasValue ? (int)Math.Round(track.DurationMs.Value / 1000d) : 0,
            TrackPosition = index + 1,
            Link = track.SourceUrl,
            Preview = preview,
            ArtistIds = artistIds,
            AlbumArtist = track.AlbumArtist ?? string.Empty,
            ExplicitLyrics = track.Explicit ?? false,
            Artist = new SpotifyTracklistArtist
            {
                Id = GetPrimaryArtistId(artistIds),
                Name = track.Artists ?? string.Empty
            },
            Album = new SpotifyTracklistAlbum
            {
                Id = track.AlbumId ?? string.Empty,
                Title = track.Album ?? string.Empty,
                CoverMedium = track.ImageUrl ?? string.Empty
            }
        };
    }

    private static SpotifyTracklistTrack CloneTrackWithDeezerId(SpotifyTracklistTrack track, string deezerId)
    {
        return new SpotifyTracklistTrack
        {
            Id = deezerId,
            Index = track.Index,
            Title = track.Title,
            Isrc = track.Isrc,
            Preview = $"/api/deezer/stream/{deezerId}",
            Duration = track.Duration,
            TrackPosition = track.TrackPosition,
            Link = track.Link,
            ArtistIds = track.ArtistIds,
            AlbumArtist = track.AlbumArtist,
            ExplicitLyrics = track.ExplicitLyrics,
            Artist = track.Artist,
            Album = track.Album
        };
    }

    private static List<string> CloneArtistIds(IReadOnlyList<string>? artistIds)
    {
        if (artistIds is null || artistIds.Count == 0)
        {
            return new List<string>();
        }

        return new List<string>(artistIds);
    }

    private static string GetPrimaryArtistId(IReadOnlyList<string>? artistIds)
    {
        if (artistIds is null || artistIds.Count == 0)
        {
            return string.Empty;
        }

        return artistIds[0] ?? string.Empty;
    }

    private async Task<SpotifyTracklistConversion> ConvertTracksAsync(
        List<SpotifyTrackSummary> tracks,
        bool allowFallbackSearch,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return new SpotifyTracklistConversion(
                new List<SpotifyTracklistTrack>(),
                new List<SpotifyTracklistPending>());
        }

        var results = new SpotifyTracklistTrack[tracks.Count];
        var pending = new System.Collections.Concurrent.ConcurrentBag<SpotifyTracklistPending>();
        var tasks = new List<Task>(tracks.Count);
        var settings = _settingsService.LoadSettings();
        var resolveConcurrency = settings.SpotifyResolveConcurrency > 0
            ? settings.SpotifyResolveConcurrency
            : 1;
        var strictSpotifyDeezerMode = settings.StrictSpotifyDeezerMode;
        using var gate = new SemaphoreSlim(resolveConcurrency, resolveConcurrency);
        var context = new ProcessTrackContext(
            allowFallbackSearch,
            strictSpotifyDeezerMode,
            results,
            pending,
            gate);

        // Resolve the first chunk immediately to avoid empty Deezer IDs in the UI/downloads.
        var resolveNowLimit = Math.Min(ImmediateResolveLimit, tracks.Count);
        for (var index = 0; index < tracks.Count; index++)
        {
            var trackIndex = index;
            var track = tracks[trackIndex];
            var resolveNow = trackIndex < resolveNowLimit;
            tasks.Add(ProcessTrackAsync(track, trackIndex, resolveNow, context, cancellationToken));
        }

        await Task.WhenAll(tasks);
        var orderedPending = pending
            .OrderBy(entry => entry.Index)
            .ToList();
        return new SpotifyTracklistConversion(results.ToList(), orderedPending);
    }

    private async Task ProcessTrackAsync(
        SpotifyTrackSummary track,
        int trackIndex,
        bool resolveNow,
        ProcessTrackContext context,
        CancellationToken cancellationToken)
    {
        await context.Gate.WaitAsync(cancellationToken);
        try
        {
            var deezerId = string.Empty;
            if (resolveNow)
            {
                deezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                    _deezerClient,
                    _songLinkResolver,
                    track,
                    new SpotifyTrackResolveOptions(
                        AllowFallbackSearch: context.AllowFallbackSearch,
                        PreferIsrcOnly: !context.AllowFallbackSearch,
                        UseSongLink: context.AllowFallbackSearch,
                        StrictMode: context.StrictSpotifyDeezerMode,
                        BypassNegativeCanonicalCache: true,
                        Logger: _logger,
                        CancellationToken: cancellationToken)) ?? string.Empty;
            }
            var normalizedDeezerId = IsNumericDeezerId(deezerId) ? deezerId : string.Empty;
            var preview = !string.IsNullOrWhiteSpace(normalizedDeezerId)
                ? $"/api/deezer/stream/{normalizedDeezerId}"
                : string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedDeezerId))
            {
                context.Pending.Add(new SpotifyTracklistPending(trackIndex, track));
            }
            context.Results[trackIndex] = CreateTracklistTrackFromSummary(
                track,
                trackIndex,
                normalizedDeezerId,
                preview);
        }
        finally
        {
            context.Gate.Release();
        }
    }

    private static bool IsNumericDeezerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return value.All(char.IsDigit);
    }
}

public sealed class SpotifyTracklistResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
    [JsonPropertyName("picture_xl")]
    public string PictureXl { get; init; } = string.Empty;
    [JsonPropertyName("picture_big")]
    public string PictureBig { get; init; } = string.Empty;
    [JsonPropertyName("creator")]
    public SpotifyTracklistCreator Creator { get; init; } = new();
    [JsonPropertyName("followers")]
    public int? Followers { get; init; }
    [JsonPropertyName("nb_tracks")]
    public int NbTracks { get; init; }
    [JsonPropertyName("tracks")]
    public List<SpotifyTracklistTrack> Tracks { get; init; } = new();
}

public sealed class SpotifyTracklistTrack
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("index")]
    public int Index { get; init; }
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
    [JsonPropertyName("isrc")]
    public string Isrc { get; init; } = string.Empty;
    [JsonPropertyName("preview")]
    public string Preview { get; init; } = string.Empty;
    [JsonPropertyName("duration")]
    public int Duration { get; init; }
    [JsonPropertyName("track_position")]
    public int TrackPosition { get; init; }
    [JsonPropertyName("link")]
    public string Link { get; init; } = string.Empty;
    [JsonPropertyName("artist_ids")]
    public List<string> ArtistIds { get; init; } = new();
    [JsonPropertyName("album_artist")]
    public string AlbumArtist { get; init; } = string.Empty;
    [JsonPropertyName("explicit_lyrics")]
    public bool ExplicitLyrics { get; init; }
    [JsonPropertyName("artist")]
    public SpotifyTracklistArtist Artist { get; init; } = new();
    [JsonPropertyName("album")]
    public SpotifyTracklistAlbum Album { get; init; } = new();
}

public sealed class SpotifyTracklistArtist
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class SpotifyTracklistAlbum
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
    [JsonPropertyName("cover_medium")]
    public string CoverMedium { get; init; } = string.Empty;
}

public sealed class SpotifyTracklistCreator
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
    [JsonPropertyName("avatar")]
    public string Avatar { get; init; } = string.Empty;
}

public sealed record SpotifyTracklistPayload(
    SpotifyTracklistResult Tracklist,
    string MatchToken,
    int PendingCount);

public sealed record SpotifyTracklistBuildResult(
    string MatchToken,
    int PendingCount,
    List<SpotifyTracklistTrack> Tracks);

public sealed record SpotifyTracklistMatchStart(string Token, int Pending);

internal sealed record VisibleResolveContext(
    int Offset,
    string? SnapshotId,
    bool AllowFallbackSearch,
    bool StrictSpotifyDeezerMode);

internal sealed record ProcessTrackContext(
    bool AllowFallbackSearch,
    bool StrictSpotifyDeezerMode,
    SpotifyTracklistTrack[] Results,
    System.Collections.Concurrent.ConcurrentBag<SpotifyTracklistPending> Pending,
    SemaphoreSlim Gate);

internal sealed record SpotifyTracklistPending(int Index, SpotifyTrackSummary Track);

internal sealed record SpotifyTracklistConversion(
    List<SpotifyTracklistTrack> Tracks,
    List<SpotifyTracklistPending> Pending);

internal static class SpotifyTracklistMapper
{
    internal static List<SpotifyTracklistTrack> MapTracks(
        IReadOnlyList<SpotifyTrackSummary> tracks,
        int offset)
    {
        var mapped = new List<SpotifyTracklistTrack>(tracks.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var index = offset + i;
            var spotifyId = SpotifyTracklistService.ExtractSpotifyTrackId(track);
            mapped.Add(SpotifyTracklistService.CreateTracklistTrackFromSummary(track, index, spotifyId, string.Empty));
        }

        return mapped;
    }
}
