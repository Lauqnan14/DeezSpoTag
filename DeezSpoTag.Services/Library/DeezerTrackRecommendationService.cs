using DeezSpoTag.Integrations.Deezer;
using Newtonsoft.Json.Linq;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DeezSpoTag.Services.Library;

public sealed class DeezerTrackRecommendationService
{
    private const int DailyLimit = 150;
    private const int SeedProbeLimit = 32;
    private const int MaxArtistOccurrences = 2;
    private const int MaxAlbumOccurrences = 2;
    private const string ArtistsKey = "ARTISTS";
    private const string AlbumKey = "album";
    private readonly LibraryRepository _repository;
    private readonly DeezerGatewayService _deezerGatewayService;
    private readonly ConcurrentDictionary<string, RecommendationDetailDto> _dailyCache = new(StringComparer.Ordinal);

    public DeezerTrackRecommendationService(
        LibraryRepository repository,
        DeezerGatewayService deezerGatewayService)
    {
        _repository = repository;
        _deezerGatewayService = deezerGatewayService;
    }

    public async Task<IReadOnlyList<RecommendationStationDto>> GetStationsAsync(
        long libraryId,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var detail = await GetDailyRecommendationsAsync(
            libraryId,
            DailyLimit,
            folderId,
            cancellationToken);
        if (detail is null)
        {
            return Array.Empty<RecommendationStationDto>();
        }

        return new[] { detail.Station with { TrackCount = detail.Tracks.Count } };
    }

    public async Task<RecommendationDetailDto?> GetDailyRecommendationsAsync(
        long libraryId,
        int limit = DailyLimit,
        long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            return null;
        }

        var cappedLimit = Math.Clamp(limit, 1, DailyLimit);
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        PruneOldCache(todayUtc);

        var cacheKey = BuildCacheKey(libraryId, folderId, todayUtc, cappedLimit);
        if (_dailyCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var normalizedLibraryIds = await LoadNormalizedLibraryIdsAsync(libraryId, folderId, cancellationToken);
        if (normalizedLibraryIds.Count == 0)
        {
            return null;
        }

        var orderedSeeds = OrderSeedsForDay(normalizedLibraryIds, todayUtc);
        var tracks = await BuildDailyRecommendationTracksAsync(
            orderedSeeds,
            normalizedLibraryIds,
            cappedLimit,
            cancellationToken);

        var station = BuildStation(todayUtc, tracks.Count);
        var detail = new RecommendationDetailDto(station, tracks, DateTimeOffset.UtcNow);
        _dailyCache[cacheKey] = detail;
        return detail;
    }

    private async Task<HashSet<string>> LoadNormalizedLibraryIdsAsync(
        long libraryId,
        long? folderId,
        CancellationToken cancellationToken)
    {
        var librarySourceIds = await _repository.GetLibraryDeezerTrackSourceIdsAsync(
            libraryId,
            folderId,
            cancellationToken);
        return new HashSet<string>(
            librarySourceIds
                .Select(NormalizeId)
                .Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.Ordinal);
    }

    private async Task<List<RecommendationTrackDto>> BuildDailyRecommendationTracksAsync(
        IReadOnlyList<string> orderedSeeds,
        HashSet<string> normalizedLibraryIds,
        int cappedLimit,
        CancellationToken cancellationToken)
    {
        var tracks = new List<RecommendationTrackDto>(cappedLimit);
        var overflowTracks = new List<RecommendationTrackDto>(cappedLimit);
        var seenRecommendationIds = new HashSet<string>(StringComparer.Ordinal);
        var artistCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var albumCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxSeedsToProbe = Math.Min(orderedSeeds.Count, SeedProbeLimit);

        for (var seedIndex = 0; seedIndex < maxSeedsToProbe; seedIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tracks.Count >= cappedLimit && overflowTracks.Count >= cappedLimit)
            {
                break;
            }

            var seedId = orderedSeeds[seedIndex];
            var mixTracks = await LoadTrackMixAsync(seedId, cancellationToken);
            AddUniqueRecommendationTracks(
                mixTracks,
                tracks,
                overflowTracks,
                normalizedLibraryIds,
                seenRecommendationIds,
                artistCounts,
                albumCounts,
                cappedLimit);
        }

        if (tracks.Count < cappedLimit && overflowTracks.Count > 0)
        {
            foreach (var overflowTrack in overflowTracks)
            {
                if (tracks.Count >= cappedLimit)
                {
                    break;
                }

                tracks.Add(overflowTrack with { TrackPosition = tracks.Count + 1 });
            }
        }

        return tracks;
    }

    private static void AddUniqueRecommendationTracks(
        IReadOnlyList<RecommendationTrackDto> sourceTracks,
        List<RecommendationTrackDto> destinationTracks,
        List<RecommendationTrackDto> overflowTracks,
        HashSet<string> normalizedLibraryIds,
        HashSet<string> seenRecommendationIds,
        Dictionary<string, int> artistCounts,
        Dictionary<string, int> albumCounts,
        int limit)
    {
        foreach (var track in sourceTracks)
        {
            if (destinationTracks.Count >= limit && overflowTracks.Count >= limit)
            {
                break;
            }

            TryAddRecommendationTrack(
                track,
                destinationTracks,
                overflowTracks,
                normalizedLibraryIds,
                seenRecommendationIds,
                artistCounts,
                albumCounts);
        }
    }

    private static void TryAddRecommendationTrack(
        RecommendationTrackDto track,
        List<RecommendationTrackDto> destinationTracks,
        List<RecommendationTrackDto> overflowTracks,
        HashSet<string> normalizedLibraryIds,
        HashSet<string> seenRecommendationIds,
        Dictionary<string, int> artistCounts,
        Dictionary<string, int> albumCounts)
    {
        var normalizedTrackId = NormalizeId(track.Id);
        if (string.IsNullOrWhiteSpace(normalizedTrackId)
            || normalizedLibraryIds.Contains(normalizedTrackId)
            || !seenRecommendationIds.Add(normalizedTrackId))
        {
            return;
        }

        if (CanAddWithDiversity(track, artistCounts, albumCounts))
        {
            destinationTracks.Add(track with { TrackPosition = destinationTracks.Count + 1 });
            IncrementDiversityCount(GetArtistDiversityKey(track), artistCounts);
            IncrementDiversityCount(GetAlbumDiversityKey(track), albumCounts);
            return;
        }

        overflowTracks.Add(track);
    }

    private static bool CanAddWithDiversity(
        RecommendationTrackDto track,
        Dictionary<string, int> artistCounts,
        Dictionary<string, int> albumCounts)
    {
        var artistKey = GetArtistDiversityKey(track);
        var albumKey = GetAlbumDiversityKey(track);
        return GetDiversityCount(artistKey, artistCounts) < MaxArtistOccurrences
               && GetDiversityCount(albumKey, albumCounts) < MaxAlbumOccurrences;
    }

    private static string GetArtistDiversityKey(RecommendationTrackDto track)
    {
        var artistId = NormalizeId(track.Artist.Id);
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            return $"artist:{artistId}";
        }

        var artistName = (track.Artist.Name ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(artistName)
            && !artistName.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            return $"artist-name:{artistName.ToLowerInvariant()}";
        }

        return string.Empty;
    }

    private static string GetAlbumDiversityKey(RecommendationTrackDto track)
    {
        var albumId = NormalizeId(track.Album.Id);
        if (!string.IsNullOrWhiteSpace(albumId))
        {
            return $"album:{albumId}";
        }

        var albumTitle = (track.Album.Title ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(albumTitle)
            && !albumTitle.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase))
        {
            return $"album-title:{albumTitle.ToLowerInvariant()}";
        }

        return string.Empty;
    }

    private static int GetDiversityCount(string key, Dictionary<string, int> counts)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return 0;
        }

        return counts.TryGetValue(key, out var value) ? value : 0;
    }

    private static void IncrementDiversityCount(string key, Dictionary<string, int> counts)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        counts[key] = GetDiversityCount(key, counts) + 1;
    }

    private async Task<IReadOnlyList<RecommendationTrackDto>> LoadTrackMixAsync(
        string sourceTrackId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var response = await _deezerGatewayService.GetContextualTrackMixAsync(new[] { sourceTrackId });
        if (response is null)
        {
            return Array.Empty<RecommendationTrackDto>();
        }

        var results = response["results"] as JObject ?? response;
        var data = results["data"] as JArray ?? results["DATA"] as JArray;
        if (data is null || data.Count == 0)
        {
            return Array.Empty<RecommendationTrackDto>();
        }

        var tracks = new List<RecommendationTrackDto>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            var token = data[i] as JObject;
            var mapped = MapTrack(token, i + 1);
            if (mapped is not null)
            {
                tracks.Add(mapped);
            }
        }

        return tracks;
    }

    private static RecommendationTrackDto? MapTrack(JObject? track, int fallbackPosition)
    {
        if (track is null)
        {
            return null;
        }

        var id = GetString(track, "SNG_ID", "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = GetString(track, "SNG_TITLE", "title");
        var version = GetString(track, "VERSION");
        if (!string.IsNullOrWhiteSpace(version))
        {
            title = string.IsNullOrWhiteSpace(title) ? version : $"{title} {version}";
        }

        var artistId = GetString(track, "ART_ID")
            ?? track["artist"]?["id"]?.ToString()
            ?? track[ArtistsKey]?.First?["ART_ID"]?.ToString()
            ?? track[ArtistsKey]?.First?["id"]?.ToString()
            ?? string.Empty;
        var artistName = GetString(track, "ART_NAME")
            ?? track["artist"]?["name"]?.ToString()
            ?? track[ArtistsKey]?.First?["ART_NAME"]?.ToString()
            ?? track[ArtistsKey]?.First?["name"]?.ToString()
            ?? "Unknown Artist";

        var albumId = GetString(track, "ALB_ID")
            ?? track[AlbumKey]?["id"]?.ToString()
            ?? string.Empty;
        var albumTitle = GetString(track, "ALB_TITLE")
            ?? track[AlbumKey]?["title"]?.ToString()
            ?? "Unknown Album";
        var albumPicture = GetString(track, "ALB_PICTURE")
            ?? track[AlbumKey]?["md5_image"]?.ToString()
            ?? track[AlbumKey]?["cover"]?.ToString()
            ?? string.Empty;

        var duration = GetInt(track, "DURATION", "duration");
        var isrc = GetString(track, "ISRC", "isrc") ?? string.Empty;
        var position = GetInt(track, "TRACK_NUMBER", "track_position", "POSITION");
        if (position <= 0)
        {
            position = fallbackPosition;
        }

        return new RecommendationTrackDto(
            NormalizeId(id),
            title ?? "Unknown",
            Math.Max(0, duration),
            isrc,
            position,
            new RecommendationArtistDto(NormalizeId(artistId), artistName),
            new RecommendationAlbumDto(
                NormalizeId(albumId),
                albumTitle,
                BuildCoverUrl(albumPicture)));
    }

    private static RecommendationStationDto BuildStation(DateOnly dayUtc, int trackCount)
    {
        var dateValue = $"{dayUtc:yyyy-MM-dd}";
        return new RecommendationStationDto(
            $"deezer-daily-{dateValue}",
            "Daily Recommendations",
            "Track-mix recommendations from your Deezer-linked library tracks.",
            "deezer-track-mix-daily",
            dateValue,
            trackCount);
    }

    private static List<string> OrderSeedsForDay(
        IReadOnlyCollection<string> sourceIds,
        DateOnly dayUtc)
    {
        return sourceIds
            .Select(sourceId => new SeedScore(sourceId, ComputeDailyScore(sourceId, dayUtc)))
            .OrderBy(item => item.Score)
            .ThenBy(item => item.SourceId, StringComparer.Ordinal)
            .Select(item => item.SourceId)
            .ToList();
    }

    private static ulong ComputeDailyScore(string sourceId, DateOnly dayUtc)
    {
        var input = $"{dayUtc:yyyyMMdd}:{sourceId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, sizeof(ulong)));
    }

    private static string BuildCacheKey(long libraryId, long? folderId, DateOnly dayUtc, int limit)
        => $"{libraryId}:{(folderId?.ToString() ?? "all")}:{dayUtc:yyyyMMdd}:{limit}";

    private void PruneOldCache(DateOnly currentDayUtc)
    {
        var currentMarker = $":{currentDayUtc:yyyyMMdd}:";
        foreach (var key in _dailyCache.Keys.Where(key => !key.Contains(currentMarker, StringComparison.Ordinal)))
        {
            _dailyCache.TryRemove(key, out _);
        }
    }

    private static string NormalizeId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string BuildCoverUrl(string? md5OrUrl)
    {
        if (string.IsNullOrWhiteSpace(md5OrUrl))
        {
            return string.Empty;
        }

        var normalized = md5OrUrl.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return normalized;
        }

        return $"https://e-cdns-images.dzcdn.net/images/cover/{normalized}/500x500-000000-80-0-0.jpg";
    }

    private static string? GetString(JObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
            {
                continue;
            }

            var value = token.Type switch
            {
                JTokenType.String => token.Value<string>(),
                JTokenType.Integer => token.Value<long>().ToString(),
                JTokenType.Float => token.Value<double>().ToString("0"),
                _ => token.ToString()
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int GetInt(JObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
            {
                continue;
            }

            if (token.Type == JTokenType.Integer && token.Value<int?>() is int integerValue)
            {
                return integerValue;
            }

            if (token.Type == JTokenType.Float && token.Value<double?>() is double floatValue)
            {
                return Convert.ToInt32(floatValue);
            }

            if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var parsedValue))
            {
                return parsedValue;
            }
        }

        return 0;
    }

    private readonly record struct SeedScore(string SourceId, ulong Score);
}
