using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

internal static class PlaylistCandidateContract
{
    public const int CurrentCacheSchemaVersion = 2;
    public const string ValidationRevision = "playlist-candidate-v2";

    public static bool IsResolvable(string source, PlaylistTrackCandidate? candidate)
    {
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.TrackSourceId))
        {
            return false;
        }

        var normalizedSource = Normalize(source);
        return normalizedSource switch
        {
            "boomplay" => !string.IsNullOrWhiteSpace(candidate.DeezerId)
                          && string.Equals(candidate.MappingStatus, BoomplayWatchlistMappingService.MatchedStatus, StringComparison.OrdinalIgnoreCase),
            "spotify" or "deezer" or "apple" or "qobuz" or "tidal" or "recommendations" or "smarttracklist" => true,
            _ => !string.IsNullOrWhiteSpace(candidate.Isrc)
                 || (!string.IsNullOrWhiteSpace(candidate.Title) && !string.IsNullOrWhiteSpace(candidate.Artist))
        };
    }

    public static bool IsReusableCache(
        string source,
        int schemaVersion,
        IReadOnlyList<PlaylistTrackCandidate>? candidates,
        int? expectedTrackCount,
        bool isComplete)
        => schemaVersion == CurrentCacheSchemaVersion
           && isComplete
           && candidates != null
           && (!expectedTrackCount.HasValue || candidates.Count == Math.Max(0, expectedTrackCount.Value))
           && candidates.All(candidate => IsResolvable(source, candidate));

    public static IReadOnlyList<PlaylistTrackCandidate> ResolvableCandidates(
        string source,
        IEnumerable<PlaylistTrackCandidate> candidates)
        => candidates.Where(candidate => IsResolvable(source, candidate)).ToList();

    public static string BuildIdentityRevision(string source, IEnumerable<PlaylistTrackCandidate> candidates)
    {
        var payload = JsonSerializer.Serialize(candidates.Select(candidate => new
        {
            source = Normalize(source),
            sourceId = Normalize(candidate.TrackSourceId),
            deezerId = Normalize(candidate.DeezerId),
            isrc = Normalize(candidate.Isrc),
            title = Normalize(candidate.Title),
            artist = Normalize(candidate.Artist),
            album = Normalize(candidate.Album),
            durationMs = candidate.DurationMs,
            mappingStatus = Normalize(candidate.MappingStatus)
        }));
        return $"{ValidationRevision}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
