using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using System.Text;
using static DeezSpoTag.Integrations.Deezer.DeezerGatewayService;
using static DeezSpoTag.Integrations.Deezer.DeezerApiService;

namespace DeezSpoTag.Integrations.Deezer;

/// <summary>
/// Consolidated Deezer client merging DeezerClient, DeezerApiService, DeezerGatewayService, and DeezerSessionManager
/// EXACT PORT from deezspotag deezer-sdk with unified interface for both API and Gateway operations
/// </summary>
public sealed class DeezerClient : IDisposable
{
    private const string SessionManagerNotSetMessage = "Session manager not set";
    private const string TrackEntity = "track";
    private const string AlbumEntity = "album";
    private const string ArtistEntity = "artist";
    private const string PlaylistEntity = "playlist";
    private const string JsonArtistKey = "artist";
    private const string JsonAlbumKey = "album";
    private readonly ILogger<DeezerClient> _logger;
    private DeezerSessionManager? _sessionManager;
    private bool _disposed;

    // Delegate to session manager
    public bool LoggedIn => _sessionManager?.LoggedIn ?? false;
    public DeezerUser? CurrentUser => _sessionManager?.CurrentUser;
    public List<DeezerUser> Children => _sessionManager?.Children ?? new List<DeezerUser>();
    public int SelectedAccount => _sessionManager?.SelectedAccount ?? 0;

    // Compatibility properties for existing code that expects Api and Gw properties
    public DeezerClient Api { get; private set; }
    public DeezerClient Gw { get; private set; }

    public DeezerClient(ILogger<DeezerClient> logger, DeezerSessionManager sessionManager)
    {
        _logger = logger;
        _sessionManager = sessionManager;

        // Initialize Api and Gw properties for compatibility
        Api = this;
        Gw = this;
    }

    public string? GetCookieValue(string name)
    {
        if (_sessionManager == null)
        {
            return null;
        }

        return _sessionManager.GetCookieValue(name);
    }

    public void SetSessionManager(DeezerSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    private DeezerSessionManager RequireSessionManager()
    {
        return _sessionManager ?? throw new InvalidOperationException(SessionManagerNotSetMessage);
    }

    /// <summary>
    /// Child accounts (for compatibility with deezspotag API)
    /// </summary>
    public IReadOnlyList<string> ChildAccounts => _sessionManager?.ChildAccounts ?? Array.Empty<string>();

    /// <summary>
    /// Login using ARL token - delegates to session manager
    /// </summary>
    public async Task<bool> LoginViaArlAsync(string arl, int child = 0)
    {
        return await RequireSessionManager().LoginViaArlAsync(arl, child);
    }

    /// <summary>
    /// Change active account - delegates to session manager
    /// </summary>
    public void ChangeAccount(int childIndex)
    {
        RequireSessionManager().ChangeAccount(childIndex);
    }

    /// <summary>
    /// Get download URL for single track - delegates to session manager
    /// </summary>
    public async Task<string?> GetTrackUrlAsync(string trackToken, string format)
    {
        var urls = await RequireSessionManager().GetTracksUrlAsync(new[] { trackToken }, format);
        return urls.FirstOrDefault();
    }

    /// <summary>
    /// Get download URL with media API status (error codes preserved).
    /// </summary>
    public async Task<DeezerMediaResult> GetTrackUrlWithStatusAsync(string trackToken, string format)
    {
        var results = await RequireSessionManager().GetTracksUrlWithStatusAsync(new[] { trackToken }, format);
        return results.FirstOrDefault() ?? DeezerMediaResult.Empty();
    }

    /// <summary>
    /// Get download URLs for multiple tracks - delegates to session manager
    /// </summary>
    public async Task<List<string?>> GetTracksUrlAsync(string[] trackTokens, string format)
    {
        return await RequireSessionManager().GetTracksUrlAsync(trackTokens, format);
    }

    /// <summary>
    /// Get download URLs with media API status (error codes preserved).
    /// </summary>
    public async Task<List<DeezerMediaResult>> GetTracksUrlWithStatusAsync(string[] trackTokens, string format)
    {
        return await RequireSessionManager().GetTracksUrlWithStatusAsync(trackTokens, format);
    }

    #region Consolidated API Methods (from DeezerApiService)

    private async Task<T> CallAsync<T>(string endpoint, Dictionary<string, object>? args = null) where T : class
    {
        return await RequireSessionManager().PublicApiCallAsync<T>(endpoint, args);
    }

    private Task<T> GetEntityAsync<T>(string entityType, string id) where T : class
        => CallAsync<T>($"{entityType}/{id}");

    private static Dictionary<string, object> CreatePagedArgs(int limit, int index)
    {
        return new Dictionary<string, object>
        {
            ["limit"] = Math.Clamp(limit, 1, 100),
            ["index"] = Math.Max(0, index)
        };
    }

    private void EnsureLoggedIn(string action)
    {
        if (!LoggedIn)
        {
            throw new InvalidOperationException($"Must be logged in to {action}");
        }
    }

    private async Task<Dictionary<string, object>?> GetGwEntityDictionaryAsync<TEntity>(
        Func<Task<TEntity>> fetchAsync,
        Func<TEntity, Dictionary<string, object>> map,
        string entityName,
        string entityId) where TEntity : class
    {
        try
        {
            var entity = await fetchAsync();
            return map(entity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get {EntityName} {EntityId}", entityName, entityId);
            return null;
        }
    }

    private static List<GwTrack> ApplyTrackPositions(List<GwTrack> source)
    {
        var tracks = new List<GwTrack>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var track = source[i];
            track.Position = i;
            tracks.Add(track);
        }

        return tracks;
    }

    // -----===== Tracks =====-----

    public Task<ApiTrack> GetTrack(string trackId) => GetEntityAsync<ApiTrack>(TrackEntity, trackId);

    public async Task<Dictionary<string, object>?> GetTrackAsync(string trackId, CancellationToken cancellationToken)
    {
        return await GetGwEntityDictionaryAsync(
            () => GetGwTrackAsync(trackId),
            ConvertGwTrackToDictionary,
            TrackEntity,
            trackId);
    }

    /// <summary>
    /// Get track information (unified interface)
    /// </summary>
    public async Task<ApiTrack> GetTrackAsync(string trackId)
    {
        EnsureLoggedIn("get track data");
        return await GetTrack(trackId);
    }

    public Task<ApiTrack> GetTrackByIsrcAsync(string isrc) => GetTrackAsync($"isrc:{isrc}");

    // -----===== Albums =====-----

    public Task<ApiAlbum> GetAlbum(string albumId) => GetEntityAsync<ApiAlbum>(AlbumEntity, albumId);

    public async Task<Dictionary<string, object>?> GetAlbumAsync(string albumId, CancellationToken cancellationToken)
    {
        return await GetGwEntityDictionaryAsync(
            () => GetGwAlbumAsync(albumId),
            ConvertGwAlbumToDictionary,
            AlbumEntity,
            albumId);
    }

    /// <summary>
    /// Get album information (unified interface)
    /// </summary>
    public async Task<ApiAlbum> GetAlbumAsync(string albumId)
    {
        EnsureLoggedIn("get album data");
        return await GetAlbum(albumId);
    }

    public Task<ApiAlbum> GetAlbumByUpcAsync(string upc) => GetAlbumAsync($"upc:{upc}");

    // -----===== Playlists =====-----

    public Task<ApiPlaylist> GetPlaylist(string playlistId) => GetEntityAsync<ApiPlaylist>(PlaylistEntity, playlistId);

    // -----===== Search =====-----

    private static Dictionary<string, object> GenerateSearchArgs(string query, ApiOptions? options = null)
    {
        options ??= new ApiOptions();
        query = CleanSearchQuery(query);
        var args = new Dictionary<string, object>
        {
            ["q"] = query,
            ["index"] = options.Index ?? 0,
            ["limit"] = options.Limit ?? 25
        };

        if (options.Strict == true)
            args["strict"] = "on";

        if (!string.IsNullOrEmpty(options.Order))
            args["order"] = options.Order;

        return args;
    }

    private Task<DeezerSearchResult> SearchEndpointAsync(string endpoint, string query, ApiOptions? options = null)
        => CallAsync<DeezerSearchResult>(endpoint, GenerateSearchArgs(query, options));

    public Task<DeezerSearchResult> SearchAsync(string query, ApiOptions? options = null)
        => SearchEndpointAsync("search", query, options);

    /// <summary>
    /// Search for content on Deezer with type selection
    /// </summary>
    public async Task<DeezerSearchResult> SearchAsync(string query, string type = TrackEntity, ApiOptions? options = null)
    {
        EnsureLoggedIn("search");

        return type.ToLower() switch
        {
            TrackEntity => await SearchTrackAsync(query, options),
            AlbumEntity => await SearchAlbumAsync(query, options),
            ArtistEntity => await SearchArtistAsync(query, options),
            PlaylistEntity => await SearchPlaylistAsync(query, options),
            _ => await SearchAsync(query, options)
        };
    }

    public Task<DeezerSearchResult> SearchTrackAsync(string query, ApiOptions? options = null)
        => SearchEndpointAsync("search/track", query, options);

    public async Task<DeezerSearchResult> SearchTracksAsync(string query, ApiOptions? options = null)
    {
        return await SearchTrackAsync(query, options);
    }

    public async Task<List<Track>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        var options = new ApiOptions { Limit = limit };
        var result = await SearchTrackAsync(query, options);
        return ConvertSearchResultTracks(result.Data);
    }

    public Task<DeezerSearchResult> GetUserPlaylistsRawAsync(string userId, int limit = 50, int index = 0)
        => CallAsync<DeezerSearchResult>($"user/{userId}/playlists", CreatePagedArgs(limit, index));

    public Task<DeezerSearchResult> GetUserTracksRawAsync(string userId, int limit = 50, int index = 0)
        => CallAsync<DeezerSearchResult>($"user/{userId}/tracks", CreatePagedArgs(limit, index));

    public Task<DeezerSearchResult> SearchAlbumAsync(string query, ApiOptions? options = null)
        => SearchEndpointAsync("search/album", query, options);

    public Task<DeezerSearchResult> SearchArtistAsync(string query, ApiOptions? options = null)
        => SearchEndpointAsync("search/artist", query, options);

    public Task<DeezerSearchResult> SearchPlaylistAsync(string query, ApiOptions? options = null)
        => SearchEndpointAsync("search/playlist", query, options);

    /// <summary>
    /// Advanced search with metadata - EXACT PORT from deezspotag api.ts get_track_id_from_metadata
    /// </summary>
    public async Task<string> GetTrackIdFromMetadataAsync(string artist, string track, string album, int? durationMs = null)
    {
        var searchInput = CreateMetadataSearchInput(artist, track, album, durationMs);
        foreach (var attempt in BuildMetadataSearchAttempts(searchInput))
        {
            var match = await SearchMetadataAttemptAsync(attempt, searchInput);
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return "0";
    }

    public async Task<string> GetTrackIdFromMetadataFastAsync(
        string artist,
        string track,
        int? durationMs = null)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            return "0";
        }

        var durationSeconds = durationMs is > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : (int?)null;
        var sourceAllowsDerivative = DeezerCandidateHeuristics.SourceAllowsDerivative(track, artist, string.Empty);

        var scoredMatches = await SearchFastMetadataCandidatesAsync(artist, track, durationSeconds, sourceAllowsDerivative);
        return ResolveBestFastMetadataMatch(scoredMatches.HighQuality, scoredMatches.ExploreMore);
    }

    private static List<Track> ConvertSearchResultTracks(object[]? data)
    {
        if (data == null)
        {
            return new List<Track>();
        }

        return data
            .OfType<System.Text.Json.JsonElement>()
            .Select(ConvertSearchResultTrack)
            .ToList();
    }

    private static Track ConvertSearchResultTrack(System.Text.Json.JsonElement jsonElement)
    {
        var track = new Track
        {
            Id = TryGetJsonElementString(jsonElement, "id"),
            Title = TryGetJsonElementString(jsonElement, "title"),
            Duration = TryGetJsonElementInt(jsonElement, "duration")
        };

        if (jsonElement.TryGetProperty(JsonArtistKey, out var artist))
        {
            track.MainArtist = new Artist
            {
                Id = TryGetJsonElementInt64(artist, "id", 0L).ToString(),
                Name = TryGetJsonElementString(artist, "name")
            };
        }

        if (jsonElement.TryGetProperty(JsonAlbumKey, out var album))
        {
            track.Album = new Album
            {
                Id = TryGetJsonElementString(album, "id"),
                Title = TryGetJsonElementString(album, "title")
            };
        }

        return track;
    }

    private static string TryGetJsonElementString(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            ? value.ToString() switch
            {
                null => string.Empty,
                _ when value.ValueKind == System.Text.Json.JsonValueKind.String => value.GetString() ?? string.Empty,
                var raw => raw
            }
            : string.Empty;
    }

    private static int TryGetJsonElementInt(System.Text.Json.JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static long TryGetJsonElementInt64(System.Text.Json.JsonElement element, string propertyName, long fallback)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
            ? number
            : fallback;
    }

    private readonly record struct MetadataSearchInput(string Artist, string Track, string Album, int? DurationSeconds);

    private readonly record struct MetadataSearchAttempt(string Query, string MatchTrack, string MatchAlbum, int Limit, bool Strict);

    private static MetadataSearchInput CreateMetadataSearchInput(string artist, string track, string album, int? durationMs)
    {
        return new MetadataSearchInput(
            NormalizeMetadataValue(artist),
            NormalizeMetadataValue(track),
            NormalizeMetadataValue(album),
            durationMs is > 0 ? (int)Math.Round(durationMs.Value / 1000d) : null);
    }

    private static string NormalizeMetadataValue(string value)
    {
        return (value ?? string.Empty).Replace("–", "-").Replace("'", "'");
    }

    private static IEnumerable<MetadataSearchAttempt> BuildMetadataSearchAttempts(MetadataSearchInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.Album))
        {
            yield return new MetadataSearchAttempt(
                $"artist:\"{input.Artist}\" track:\"{input.Track}\" album:\"{input.Album}\"",
                input.Track,
                input.Album,
                10,
                true);
        }

        yield return new MetadataSearchAttempt(
            $"artist:\"{input.Artist}\" track:\"{input.Track}\"",
            input.Track,
            input.Album,
            10,
            true);

        var cleanedTrack = TryGetCleanTrackTitle(input.Track);
        if (!string.IsNullOrWhiteSpace(cleanedTrack))
        {
            yield return new MetadataSearchAttempt(
                $"artist:\"{input.Artist}\" track:\"{cleanedTrack}\"",
                cleanedTrack,
                input.Album,
                10,
                true);
        }

        yield return new MetadataSearchAttempt(
            $"artist:\"{input.Artist}\" track:\"{input.Track}\"",
            input.Track,
            string.Empty,
            25,
            false);

        var looseQuery = $"{input.Artist} {input.Track}".Trim();
        if (!string.IsNullOrWhiteSpace(looseQuery))
        {
            yield return new MetadataSearchAttempt(looseQuery, input.Track, string.Empty, 50, false);
        }

        if (!string.IsNullOrWhiteSpace(input.Track))
        {
            yield return new MetadataSearchAttempt(input.Track, input.Track, string.Empty, 50, false);
        }
    }

    private static string? TryGetCleanTrackTitle(string track)
    {
        if (track.Contains('(') && track.Contains(')') && track.IndexOf('(') < track.IndexOf(')'))
        {
            return track.Split('(')[0].Trim();
        }

        if (track.Contains(" - ", StringComparison.Ordinal))
        {
            return track.Split(" - ")[0].Trim();
        }

        return null;
    }

    private async Task<string?> SearchMetadataAttemptAsync(MetadataSearchAttempt attempt, MetadataSearchInput input)
    {
        var result = await SearchTrackAsync(attempt.Query, new ApiOptions { Limit = attempt.Limit, Strict = attempt.Strict });
        return SelectBestMetadataMatch(result.Data, input.Artist, attempt.MatchTrack, attempt.MatchAlbum, input.DurationSeconds);
    }

    private async Task<(Dictionary<string, int> HighQuality, Dictionary<string, int> ExploreMore)> SearchFastMetadataCandidatesAsync(
        string artist,
        string track,
        int? durationSeconds,
        bool sourceAllowsDerivative)
    {
        const int threshold = 40;
        var highQuality = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var exploreMore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in BuildFastMetadataQueries(artist, track))
        {
            var result = await SearchTrackAsync(query, new ApiOptions { Limit = 3, Strict = true });
            foreach (var candidate in DeezerMetadataMatchHelper.ConvertSearchResults(result.Data))
            {
                var score = DeezerCandidateHeuristics.ScoreFastMatch(
                    candidate,
                    track,
                    artist,
                    durationSeconds,
                    sourceAllowsDerivative,
                    GenericScoreCompilationPenalty);

                var target = score >= threshold ? highQuality : exploreMore;
                if (!target.TryGetValue(candidate.Id, out var existing) || score > existing)
                {
                    target[candidate.Id] = score;
                }
            }
        }

        return (highQuality, exploreMore);
    }

    private static IEnumerable<string> BuildFastMetadataQueries(string artist, string track)
    {
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var combined = $"{artist} {track}".Trim();
            if (!string.IsNullOrWhiteSpace(combined) && !combined.Contains("undefined", StringComparison.OrdinalIgnoreCase))
            {
                yield return combined;
            }
        }

        var titleOnly = track.Trim();
        if (!string.IsNullOrWhiteSpace(titleOnly) && !titleOnly.Contains("undefined", StringComparison.OrdinalIgnoreCase))
        {
            yield return titleOnly;
        }
    }

    private static string ResolveBestFastMetadataMatch(
        Dictionary<string, int> highQuality,
        Dictionary<string, int> exploreMore)
    {
        var bestHigh = highQuality.OrderByDescending(entry => entry.Value).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(bestHigh.Key))
        {
            return bestHigh.Key;
        }

        var bestExplore = exploreMore.OrderByDescending(entry => entry.Value).FirstOrDefault();
        return !string.IsNullOrWhiteSpace(bestExplore.Key) ? bestExplore.Key : "0";
    }

    private readonly record struct SongSeekCandidateMatch(string? Id, double Score);

    private static SongSeekCandidateMatch FindBestSongSeekCandidate(
        IReadOnlyCollection<ApiTrack> candidates,
        string title,
        string artist,
        string album,
        int? durationSeconds,
        bool sourceAllowsDerivative)
    {
        var songSeekInput = new SongSeekTrackMatcher.TrackIdentity(title, artist, album, durationSeconds);
        var bestScore = double.MinValue;
        string? bestId = null;

        foreach (var track in candidates)
        {
            if (!CanUseMetadataCandidate(track, sourceAllowsDerivative))
            {
                continue;
            }

            var candidateArtist = track.Artist?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(candidateArtist))
            {
                continue;
            }

            var candidate = new SongSeekTrackMatcher.Candidate(
                track.Title,
                candidateArtist,
                track.Album?.Title,
                track.Duration > 0 ? track.Duration : null,
                track.Id);

            var scored = SongSeekTrackMatcher.ScoreTrackMatch(songSeekInput, candidate);
            var adjustedScore = DeezerCandidateHeuristics.IsCompilationLikeCandidate(track)
                ? scored.Score - SongSeekCompilationPenalty
                : scored.Score;

            if (adjustedScore > bestScore)
            {
                bestScore = adjustedScore;
                bestId = candidate.Id;
            }
        }

        return new SongSeekCandidateMatch(bestId, bestScore);
    }

    private static ApiTrack? FindBestExactMetadataMatch(
        IReadOnlyCollection<ApiTrack> candidates,
        string normalizedArtist,
        string normalizedTitle,
        string normalizedTitleNoFeat,
        string normalizedAlbum,
        int? durationSeconds,
        bool sourceAllowsDerivative)
    {
        ApiTrack? bestMatch = null;
        var bestScore = int.MinValue;

        foreach (var track in candidates)
        {
            if (!CanUseMetadataCandidate(track, sourceAllowsDerivative) ||
                !DeezerMetadataMatchHelper.IsArtistMatch(normalizedArtist, track) ||
                !DeezerMetadataMatchHelper.IsTitleMatch(normalizedTitle, normalizedTitleNoFeat, track) ||
                !HasMatchingDuration(durationSeconds, track.Duration) ||
                !HasMatchingAlbum(normalizedAlbum, track.Album?.Title))
            {
                continue;
            }

            var score = ScoreExactMetadataMatch(track, normalizedTitle, normalizedAlbum, durationSeconds);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = track;
            }
        }

        return bestMatch;
    }

    private static ApiTrack? FindBestFuzzyMetadataMatch(
        IReadOnlyCollection<ApiTrack> candidates,
        string normalizedArtist,
        string normalizedTitle,
        string normalizedTitleNoFeat,
        string normalizedAlbum,
        int? durationSeconds,
        bool sourceAllowsDerivative)
    {
        ApiTrack? bestFuzzy = null;
        var bestScore = double.MinValue;

        foreach (var track in candidates)
        {
            if (!CanUseMetadataCandidate(track, sourceAllowsDerivative))
            {
                continue;
            }

            var titleScore = DeezerMetadataMatchHelper.GetBestTitleSimilarity(normalizedTitle, normalizedTitleNoFeat, track);
            if (titleScore < MinTitleSimilarity)
            {
                continue;
            }

            var artistScore = DeezerMetadataMatchHelper.GetBestArtistSimilarity(normalizedArtist, track);
            var durationDiff = GetAllowedDurationDifference(durationSeconds, track.Duration);
            if (durationDiff == int.MinValue)
            {
                continue;
            }

            if (!HasAcceptableArtistSimilarity(normalizedArtist, artistScore, titleScore, durationDiff == null ? null : durationDiff.Value))
            {
                continue;
            }

            var score = ScoreFuzzyMetadataMatch(track, normalizedAlbum, titleScore, artistScore, durationDiff);
            if (score > bestScore)
            {
                bestScore = score;
                bestFuzzy = track;
            }
        }

        return bestFuzzy;
    }

    private static bool CanUseMetadataCandidate(ApiTrack track, bool sourceAllowsDerivative)
    {
        return sourceAllowsDerivative || !DeezerCandidateHeuristics.IsVariantCandidate(track);
    }

    private static bool HasMatchingDuration(int? durationSeconds, int trackDuration)
    {
        return !durationSeconds.HasValue || DeezerMetadataMatchHelper.IsDurationMatch(durationSeconds.Value, trackDuration);
    }

    private static bool HasMatchingAlbum(string normalizedAlbum, string? albumTitle)
    {
        return string.IsNullOrWhiteSpace(normalizedAlbum)
            || DeezerMetadataMatchHelper.IsExactNormalizedMatch(normalizedAlbum, albumTitle);
    }

    private static int ScoreExactMetadataMatch(ApiTrack track, string normalizedTitle, string normalizedAlbum, int? durationSeconds)
    {
        var score = DeezerMetadataMatchHelper.IsExactNormalizedMatch(normalizedTitle, track.Title) ? 4 : 3;

        if (!string.IsNullOrWhiteSpace(normalizedAlbum) &&
            DeezerMetadataMatchHelper.IsExactNormalizedMatch(normalizedAlbum, track.Album?.Title))
        {
            score += 1;
        }

        if (durationSeconds.HasValue)
        {
            score -= Math.Abs(track.Duration - durationSeconds.Value);
        }

        if (DeezerCandidateHeuristics.IsCompilationLikeCandidate(track))
        {
            score -= ExactCompilationPenalty;
        }

        return score;
    }

    private static int? GetAllowedDurationDifference(int? durationSeconds, int trackDuration)
    {
        if (!durationSeconds.HasValue || trackDuration <= 0)
        {
            return null;
        }

        var durationDiff = Math.Abs(trackDuration - durationSeconds.Value);
        return durationDiff > FuzzyDurationToleranceSeconds ? int.MinValue : durationDiff;
    }

    private static bool HasAcceptableArtistSimilarity(string normalizedArtist, double artistScore, double titleScore, int? durationDiff)
    {
        var allowWeakArtist = titleScore >= HighTitleSimilarity &&
            (!durationDiff.HasValue || durationDiff.Value <= HighTitleDurationToleranceSeconds);

        return string.IsNullOrWhiteSpace(normalizedArtist)
            || artistScore >= MinArtistSimilarity
            || allowWeakArtist;
    }

    private static double ScoreFuzzyMetadataMatch(ApiTrack track, string normalizedAlbum, double titleScore, double artistScore, int? durationDiff)
    {
        var albumScore = string.IsNullOrWhiteSpace(normalizedAlbum)
            ? 0d
            : DeezerMetadataMatchHelper.ComputeSimilarity(normalizedAlbum, DeezerMetadataMatchHelper.NormalizeMatchToken(track.Album?.Title));

        var score = (titleScore * 6d) + (artistScore * 3d) + albumScore;
        if (durationDiff.HasValue)
        {
            score -= durationDiff.Value / 10d;
        }

        if (DeezerCandidateHeuristics.IsCompilationLikeCandidate(track))
        {
            score -= FuzzyCompilationPenalty;
        }

        return score;
    }


    private const double MinTitleSimilarity = 0.78;
    private const double MinArtistSimilarity = 0.72;
    private const double HighTitleSimilarity = 0.9;
    private const int HighTitleDurationToleranceSeconds = 4;
    private const int FuzzyDurationToleranceSeconds = 10;
    private const double SongSeekCompilationPenalty = 0.08;
    private const int ExactCompilationPenalty = 2;
    private const double FuzzyCompilationPenalty = 0.75;
    private const int GenericScoreCompilationPenalty = 12;
    private static string? SelectBestMetadataMatch(
        object[]? data,
        string artist,
        string title,
        string album,
        int? durationSeconds)
    {
        if (data == null || data.Length == 0)
        {
            return null;
        }

        var normalizedArtist = DeezerMetadataMatchHelper.NormalizeMatchToken(artist);
        var normalizedTitle = DeezerMetadataMatchHelper.NormalizeMatchToken(title);
        var normalizedTitleNoFeat = DeezerMetadataMatchHelper.NormalizeMatchToken(DeezerMetadataMatchHelper.RemoveFeaturing(title));
        var normalizedAlbum = DeezerMetadataMatchHelper.NormalizeMatchToken(album);
        var sourceAllowsDerivative = DeezerCandidateHeuristics.SourceAllowsDerivative(title, artist, album);

        var candidates = DeezerMetadataMatchHelper.ConvertSearchResults(data);

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestSongSeek = FindBestSongSeekCandidate(candidates, title, artist, album, durationSeconds, sourceAllowsDerivative);

        if (!string.IsNullOrWhiteSpace(bestSongSeek.Id) && bestSongSeek.Score >= SongSeekTrackMatcher.PerfectThreshold)
        {
            return bestSongSeek.Id;
        }

        var bestExact = FindBestExactMetadataMatch(
            candidates,
            normalizedArtist,
            normalizedTitle,
            normalizedTitleNoFeat,
            normalizedAlbum,
            durationSeconds,
            sourceAllowsDerivative);

        if (bestExact != null)
        {
            return bestExact.Id;
        }

        return FindBestFuzzyMetadataMatch(
            candidates,
            normalizedArtist,
            normalizedTitle,
            normalizedTitleNoFeat,
            normalizedAlbum,
            durationSeconds,
            sourceAllowsDerivative)?.Id;
    }

    #endregion

    #region Consolidated Gateway Methods (from DeezerGatewayService)

    private async Task<T> ApiCallAsync<T>(string method, object? args = null, Dictionary<string, object>? parameters = null) where T : class
    {
        return await RequireSessionManager().GatewayApiCallAsync<T>(method, args, parameters);
    }

    public async Task<T> GatewayApiCallAsync<T>(string method, object? args = null, Dictionary<string, object>? parameters = null) where T : class
    {
        return await ApiCallAsync<T>(method, args, parameters);
    }

    // -----===== Core Gateway Methods =====-----

    public async Task<DeezerUserData> GetUserDataAsync()
    {
        return await ApiCallAsync<DeezerUserData>("deezer.getUserData");
    }

    public async Task<JObject> GetUserProfilePageAsync(string userId, string tab, int limit = 10)
    {
        return await ApiCallAsync<JObject>("deezer.pageProfile", new
        {
            USER_ID = userId,
            tab,
            nb = limit
        });
    }

    public async Task<JObject> GetUserFavoriteIdsAsync(int limit = 10000, int start = 0)
    {
        return await ApiCallAsync<JObject>("song.getFavoriteIds", new
        {
            nb = limit,
            start
        });
    }

    /// <summary>
    /// Get child accounts for family accounts
    /// Ported from: /deezspotag/deezer-sdk/src/gw.ts get_child_accounts method
    /// </summary>
    public async Task<List<GwChildAccount>> GetChildAccountsAsync()
    {
        return await ApiCallAsync<List<GwChildAccount>>("deezer.getChildAccounts");
    }

    // -----===== Gateway Tracks =====-----

    public async Task<GwTrack> GetGwTrackAsync(string trackId)
    {
        return await ApiCallAsync<GwTrack>("song.getData", new { SNG_ID = trackId });
    }

    public async Task<GwTrack> GetTrackWithFallbackAsync(string trackId)
    {
        // EXACT PORT of deezspotag get_track_with_fallback method
        GwTrackPageResponse? body = null;

        if (int.TryParse(trackId, out var id) && id > 0)
        {
            try
            {
                // EXACT PORT: First try get_track_page (deezer.pageTrack) which has full data
                body = await ApiCallAsync<GwTrackPageResponse>("deezer.pageTrack", new { SNG_ID = trackId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // EXACT PORT: Fallback to basic track data if pageTrack fails
                body = null;
            }
        }

        if (body?.Data != null)
        {
            // EXACT PORT: Merge lyrics and ISRC data like deezspotag does
            var track = body.Data;
            if (!string.IsNullOrEmpty(body.Lyrics))
                track.Lyrics = body.Lyrics;
            if (body.Isrc != null)
                track.AlbumFallback = body.Isrc.ToString();
            return track;
        }
        else
        {
            // EXACT PORT: Fallback to getTrack (song.getData) - this has limited data but better than nothing
            return await GetGwTrackAsync(trackId);
        }
    }

    public async Task<List<GwTrack>> GetTracksAsync(List<string> trackIds)
    {
        var tracks = new List<GwTrack>();
        var response = await ApiCallAsync<GwTracksResponse>("song.getListData", new { SNG_IDS = trackIds });

        var responseIndex = 0;
        for (var i = 0; i < trackIds.Count; i++)
        {
            if (trackIds[i] != "0")
            {
                if (responseIndex < response.Data.Count)
                {
                    tracks.Add(response.Data[responseIndex]);
                    responseIndex++;
                }
                else
                {
                    tracks.Add(new GwTrack { SngId = 0L });
                }
            }
            else
            {
                tracks.Add(new GwTrack { SngId = 0L }); // Empty track object
            }
        }

        return tracks;
    }

    // -----===== Gateway Albums =====-----

    public async Task<GwAlbum> GetGwAlbumAsync(string albumId)
    {
        return await ApiCallAsync<GwAlbum>("album.getData", new { ALB_ID = albumId });
    }

    public async Task<List<GwTrack>> GetAlbumTracksAsync(string albumId)
    {
        var response = await ApiCallAsync<GwAlbumTracksResponse>("song.getListByAlbum", new
        {
            ALB_ID = albumId,
            nb = -1
        });
        return ApplyTrackPositions(response.Data);
    }

    // -----===== Gateway Artists =====-----

    public async Task<GwArtist> GetGwArtistAsync(string artistId)
    {
        return await ApiCallAsync<GwArtist>("artist.getData", new { ART_ID = artistId });
    }

    public async Task<List<GwTrack>> GetArtistTopTracksAsync(string artistId, int limit = 100)
    {
        var response = await ApiCallAsync<GwArtistTopResponse>("artist.getTopTrack", new
        {
            ART_ID = artistId,
            nb = limit
        });
        return ApplyTrackPositions(response.Data);
    }

    public async Task<GwDiscographyResponse> GetArtistDiscographyAsync(string artistId, int index = 0, int limit = 25)
    {
        return await ApiCallAsync<GwDiscographyResponse>("album.getDiscography", new
        {
            ART_ID = artistId,
            discography_mode = "all",
            nb = limit,
            nb_songs = 0,
            start = index
        });
    }

    /// <summary>
    /// Get artist discography tabs exactly like deezspotag does
    /// </summary>
    public async Task<Dictionary<string, List<object>>> GetArtistDiscographyTabsAsync(string artistId, int limit = 100)
    {
        var result = CreateArtistDiscographyBuckets();
        var releases = await LoadArtistDiscographyReleasesAsync(artistId, limit);
        var roleIdCounts = releases.GroupBy(r => r.RoleId).ToDictionary(g => g.Key, g => g.Count());
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Retrieved {TotalReleases} releases with role IDs: {RoleIdCounts}",
                releases.Count, string.Join(", ", roleIdCounts.Select(kvp => $"{kvp.Key}:{kvp.Value}")));        }

        var processedIds = new HashSet<string>();
        foreach (var release in releases.Where(release => processedIds.Add(release.AlbId)))
        {
            CategorizeArtistDiscographyRelease(result, artistId, release);
        }

        return result;
    }

    private static Dictionary<string, List<object>> CreateArtistDiscographyBuckets()
    {
        return new Dictionary<string, List<object>>
        {
            ["all"] = new List<object>(),
            ["featured"] = new List<object>(),
            ["more"] = new List<object>()
        };
    }

    private async Task<List<GwAlbumRelease>> LoadArtistDiscographyReleasesAsync(string artistId, int limit)
    {
        var releases = new List<GwAlbumRelease>();
        var index = 0;
        GwDiscographyResponse response;
        do
        {
            response = await GetArtistDiscographyAsync(artistId, index, limit);
            releases.AddRange(response.Data);
            index += limit;
        } while (index < response.Total);

        return releases;
    }

    private void CategorizeArtistDiscographyRelease(
        Dictionary<string, List<object>> result,
        string artistId,
        GwAlbumRelease release)
    {
        var mappedAlbum = MapArtistAlbum(release);
        _logger.LogDebug("Processing release AlbumId: ArtId=ArtId, RoleId=RoleId, IsOfficial=IsOfficial, TargetArtist=TargetArtist");

        if (IsMainArtistDiscographyRelease(artistId, release))
        {
            AddMainArtistDiscographyRelease(result, mappedAlbum);
            return;
        }

        if (release.RoleId == 5)
        {
            result["featured"].Add(mappedAlbum);
            _logger.LogDebug("Added to featured releases (ROLE_ID=5)");
            return;
        }

        if (release.RoleId == 0)
        {
            result["more"].Add(mappedAlbum);
            result["all"].Add(mappedAlbum);
            _logger.LogDebug("Added to more releases (ROLE_ID=0)");
            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Unhandled ROLE_ID {RoleId} for release {AlbumId}", release.RoleId, release.AlbId);        }
    }

    private static bool IsMainArtistDiscographyRelease(string artistId, GwAlbumRelease release)
    {
        return (release.ArtId.ToString() == artistId ||
                (release.ArtId.ToString() != artistId && release.RoleId == 0)) &&
               release.ArtistsAlbumsIsOfficial;
    }

    private void AddMainArtistDiscographyRelease(Dictionary<string, List<object>> result, Dictionary<string, object> mappedAlbum)
    {
        result["all"].Add(mappedAlbum);
        var recordType = mappedAlbum["record_type"]?.ToString() ?? "album";
        if (!result.TryGetValue(recordType, out var releases))
        {
            releases = new List<object>();
            result[recordType] = releases;
        }
        releases.Add(mappedAlbum);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Added to main releases: {RecordType}", recordType);        }
    }

    /// <summary>
    /// Maps GW album release to standard format exactly like deezspotag map_artist_album
    /// </summary>
    private static Dictionary<string, object> MapArtistAlbum(GwAlbumRelease album)
    {
        var releaseTypes = new[] { "single", "album", "compile", "ep", "bundle" };
        var roleIds = new string?[] { "Main", null, null, null, null, "Featured" };

        var recordType = "unknown";
        if (album.Type >= 0 && album.Type < releaseTypes.Length)
        {
            recordType = releaseTypes[album.Type];
        }

        // Determine the best release date to use - prioritize in order of preference
        var releaseDate = "";
        if (!string.IsNullOrEmpty(album.PhysicalReleaseDate))
        {
            releaseDate = album.PhysicalReleaseDate;
        }
        else if (!string.IsNullOrEmpty(album.DigitalReleaseDate))
        {
            releaseDate = album.DigitalReleaseDate;
        }
        else if (!string.IsNullOrEmpty(album.OriginalReleaseDate))
        {
            releaseDate = album.OriginalReleaseDate;
        }

        var artistRole = album.RoleId >= 0 && album.RoleId < roleIds.Length
            ? roleIds[album.RoleId]
            : null;

        return new Dictionary<string, object>
        {
            ["id"] = album.AlbId,
            ["title"] = album.AlbTitle ?? "",
            ["link"] = $"https://www.deezer.com/album/{album.AlbId}",
            ["cover"] = $"https://api.deezer.com/album/{album.AlbId}/image",
            ["cover_small"] = $"https://cdns-images.dzcdn.net/images/cover/{album.AlbPicture}/56x56-000000-80-0-0.jpg",
            ["cover_medium"] = $"https://cdns-images.dzcdn.net/images/cover/{album.AlbPicture}/250x250-000000-80-0-0.jpg",
            ["cover_big"] = $"https://cdns-images.dzcdn.net/images/cover/{album.AlbPicture}/500x500-000000-80-0-0.jpg",
            ["cover_xl"] = $"https://cdns-images.dzcdn.net/images/cover/{album.AlbPicture}/1000x1000-000000-80-0-0.jpg",
            ["md5_image"] = album.AlbPicture ?? "",
            ["genre_id"] = album.GenreId,
            ["fans"] = null!,
            ["release_date"] = releaseDate,
            ["record_type"] = recordType,
            ["tracklist"] = $"https://api.deezer.com/album/{album.AlbId}/tracks",
            ["explicit_lyrics"] = album.ExplicitLyrics,
            ["type"] = album.Type,
            ["nb_tracks"] = album.NumberTrack,
            ["nb_disk"] = album.NumberDisk,
            ["copyright"] = album.Copyright ?? "",
            ["rank"] = album.Rank,
            ["digital_release_date"] = album.DigitalReleaseDate ?? "",
            ["original_release_date"] = album.OriginalReleaseDate ?? "",
            ["physical_release_date"] = album.PhysicalReleaseDate ?? "",
            ["is_official"] = album.ArtistsAlbumsIsOfficial,
            ["explicit_content_cover"] = album.ExplicitAlbumContent?.ExplicitLyricsStatus ?? 0,
            ["explicit_content_lyrics"] = album.ExplicitAlbumContent?.ExplicitCoverStatus ?? 0,
            ["artist_role"] = artistRole ?? string.Empty
        };
    }

    // -----===== Gateway Playlists =====-----

    public async Task<GwPlaylistPageResponse> GetGwPlaylistAsync(string playlistId)
    {
        return await GetPlaylistPageAsync(playlistId);
    }

    public async Task<List<GwTrack>> GetPlaylistTracksAsync(string playlistId)
    {
        var response = await ApiCallAsync<GwPlaylistTracksResponse>("playlist.getSongs", new
        {
            PLAYLIST_ID = playlistId,
            nb = -1
        });
        return ApplyTrackPositions(response.Data);
    }

    // -----===== Gateway Search =====-----

    public async Task<GwSearchResponse> GwSearchAsync(string query, int index = 0, int limit = 10,
        bool suggest = true, bool artistSuggest = true, bool topTracks = true)
    {
        query = CleanSearchQuery(query);
        return await ApiCallAsync<GwSearchResponse>("deezer.pageSearch", new
        {
            query,
            start = index,
            nb = limit,
            suggest,
            artist_suggest = artistSuggest,
            top_tracks = topTracks
        });
    }

    private static string CleanSearchQuery(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return term;
        }

        var cleaned = term;
        cleaned = ReplaceWithTimeout(cleaned, @" feat[.]? ", " ", RegexOptions.IgnoreCase);
        cleaned = ReplaceWithTimeout(cleaned, @" ft[.]? ", " ", RegexOptions.IgnoreCase);
        cleaned = ReplaceWithTimeout(cleaned, @"\(feat[.]? ", " ", RegexOptions.IgnoreCase);
        cleaned = ReplaceWithTimeout(cleaned, @"\(ft[.]? ", " ", RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace(" & ", " ").Replace("–", "-").Replace("—", "-");
        return cleaned;
    }

    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, TimeSpan.FromMilliseconds(250));

    // -----===== Gateway Lyrics =====-----

    public async Task<Dictionary<string, object>?> GetLyricsAsync(string trackId, CancellationToken cancellationToken = default)
    {
        try
        {
            // EXACT PORT: Use SNG_ID like deezspotag get_track_lyrics method
            return await ApiCallAsync<Dictionary<string, object>>("song.getLyrics", new { SNG_ID = trackId });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get lyrics for track ID {TrackId}", trackId);
            return null;
        }
    }

    /// <summary>
    /// Enhanced lyrics fetching using track ID (for new lyrics service integration)
    /// </summary>
    public async Task<Dictionary<string, object>?> GetLyricsByTrackIdAsync(string trackId, CancellationToken cancellationToken = default)
    {
        return await GetLyricsAsync(trackId, cancellationToken);
    }

    /// <summary>
    /// Get track page data including lyrics (for fallback lyrics fetching)
    /// </summary>
    public async Task<Dictionary<string, object>?> GetTrackPageDataAsync(string trackId, CancellationToken cancellationToken = default)
    {
        try
        {
            var trackPage = await ApiCallAsync<GwTrackPageResponse>("deezer.pageTrack", new { SNG_ID = trackId });

            var result = new Dictionary<string, object>
            {
                ["DATA"] = ConvertGwTrackToDictionary(trackPage.Data)
            };

            if (!string.IsNullOrEmpty(trackPage.Lyrics))
            {
                // Parse lyrics JSON if available
                try
                {
                    var lyricsData = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(trackPage.Lyrics);
                    if (lyricsData != null)
                    {
                        result["LYRICS"] = lyricsData;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // If parsing fails, store as string
                    result["LYRICS"] = trackPage.Lyrics;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get track page data for ID {TrackId}", trackId);
            return null;
        }
    }

    // Helper methods to convert GW objects to dictionaries
    private static Dictionary<string, object> ConvertGwTrackToDictionary(GwTrack track)
    {
        return new Dictionary<string, object>
        {
            ["SNG_ID"] = track.SngId,
            ["SNG_TITLE"] = track.SngTitle,
            ["DURATION"] = track.Duration,
            ["MD5_ORIGIN"] = track.Md5Origin,
            ["MEDIA_VERSION"] = track.MediaVersion,
            ["FILESIZE"] = track.Filesize,
            ["FILESIZE_MP3_128"] = track.FilesizeMp3128 ?? 0,
            ["FILESIZE_MP3_320"] = track.FilesizeMp3320 ?? 0,
            ["FILESIZE_FLAC"] = track.FilesizeFlac ?? 0,
            ["ALB_ID"] = track.AlbId,
            ["ALB_TITLE"] = track.AlbTitle,
            ["ALB_PICTURE"] = track.AlbPicture,
            ["ART_ID"] = track.ArtId,
            ["ART_NAME"] = track.ArtName,
            ["ISRC"] = track.Isrc,
            ["TRACK_TOKEN"] = track.TrackToken,
            ["TRACK_TOKEN_EXPIRE"] = track.TrackTokenExpire,
            ["TRACK_NUMBER"] = track.TrackNumber,
            ["DISK_NUMBER"] = track.DiskNumber,
            ["EXPLICIT_LYRICS"] = track.ExplicitLyrics,
            ["GAIN"] = track.Gain,
            ["LYRICS_ID"] = track.LyricsId ?? "0",
            ["COPYRIGHT"] = track.Copyright ?? "",
            ["PHYSICAL_RELEASE_DATE"] = track.PhysicalReleaseDate ?? ""
        };
    }

    private static Dictionary<string, object> ConvertGwAlbumToDictionary(GwAlbum album)
    {
        return new Dictionary<string, object>
        {
            ["ALB_ID"] = album.AlbId,
            ["ALB_TITLE"] = album.AlbTitle,
            ["ALB_PICTURE"] = album.AlbPicture,
            ["ART_ID"] = album.ArtId,
            ["ART_NAME"] = album.ArtName,
            ["PHYSICAL_RELEASE_DATE"] = ""
        };
    }

    private static Dictionary<string, object> ConvertGwArtistToDictionary(GwArtist artist)
    {
        return new Dictionary<string, object>
        {
            ["ART_ID"] = artist.ArtId,
            ["ART_NAME"] = artist.ArtName,
            ["ART_PICTURE"] = artist.ArtPicture,
            ["PictureId"] = artist.ArtPicture
        };
    }

    #endregion

    #region Unified Interface Methods

    /// <summary>
    /// Get album page with tracks (uses Gateway API)
    /// </summary>
    public async Task<GwAlbumPageResponse> GetAlbumPageAsync(string albumId)
    {
        EnsureLoggedIn("get album data");
        var language = CurrentUser?.Language ?? "en";
        return await ApiCallAsync<GwAlbumPageResponse>("deezer.pageAlbum", new
        {
            ALB_ID = albumId,
            lang = language,
            header = true,
            tab = 0
        });
    }

    /// <summary>
    /// Get playlist information (unified interface)
    /// </summary>
    public async Task<ApiPlaylist> GetPlaylistAsync(string playlistId)
    {
        EnsureLoggedIn("get playlist data");
        return await GetPlaylist(playlistId);
    }

    /// <summary>
    /// Get playlist page with tracks (uses Gateway API)
    /// </summary>
    public async Task<GwPlaylistPageResponse> GetPlaylistPageAsync(string playlistId)
    {
        EnsureLoggedIn("get playlist data");
        var language = CurrentUser?.Language ?? "en";
        return await ApiCallAsync<GwPlaylistPageResponse>("deezer.pagePlaylist", new
        {
            PLAYLIST_ID = playlistId,
            lang = language,
            header = true,
            tab = 0
        });
    }

    /// <summary>
    /// Get artist information (unified interface)
    /// </summary>
    public async Task<ApiArtist> GetArtistAsync(string artistId)
    {
        EnsureLoggedIn("get artist data");
        return await CallAsync<ApiArtist>($"artist/{artistId}");
    }

    public async Task<Dictionary<string, object>?> GetArtistAsync(string artistId, CancellationToken cancellationToken)
    {
        return await GetGwEntityDictionaryAsync(
            () => GetGwArtistAsync(artistId),
            ConvertGwArtistToDictionary,
            ArtistEntity,
            artistId);
    }

    /// <summary>
    /// Check if user can stream at specified bitrate
    /// </summary>
    public bool CanStreamAtBitrate(int bitrate)
    {
        if (CurrentUser == null) return false;

        return bitrate switch
        {
            9 => CurrentUser.CanStreamLossless == true, // FLAC
            3 => CurrentUser.CanStreamHq == true,       // MP3 320
            _ => true                                    // Lower bitrates are always available
        };
    }

    /// <summary>
    /// Logout user and clear session data - delegates to session manager
    /// </summary>
    public async Task LogoutAsync()
    {
        if (_sessionManager != null)
            await _sessionManager.LogoutAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sessionManager?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion
}
