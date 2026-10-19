using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

internal static class PlaylistCandidateContract
{
    public const int CurrentCacheSchemaVersion = 4;
    public const string ValidationRevision = "playlist-candidate-v4";

    public static bool IsResolvable(string source, PlaylistTrackCandidate? candidate)
    {
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.TrackSourceId))
        {
            return false;
        }

        var normalizedSource = Normalize(source);
        return normalizedSource switch
        {
            "ytmusic" => string.Equals(DeezSpoTag.Integrations.YouTube.YouTubeMusicSearchClient.GetVideoId(candidate.SourceUrl), candidate.TrackSourceId, StringComparison.Ordinal),
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
        bool isComplete,
        int? sourceItemsObserved = null)
        => schemaVersion == CurrentCacheSchemaVersion
           && isComplete
           && candidates != null
           && (!sourceItemsObserved.HasValue || sourceItemsObserved.Value >= candidates.Count)
           && (!expectedTrackCount.HasValue
               || (sourceItemsObserved ?? candidates.Count) == Math.Max(0, expectedTrackCount.Value));

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
            sourcePosition = candidate.SourcePosition,
            mappingStatus = Normalize(candidate.MappingStatus)
        }));
        return $"{ValidationRevision}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
    }

    public static string BuildTargetSyncRevision(
        string candidateIdentityRevision,
        IEnumerable<DeezSpoTag.Services.Library.PlaylistWatchTrackStatusDto> trackStatuses)
    {
        var localIdentityPayload = JsonSerializer.Serialize(trackStatuses
            .Where(static status => status.LocalTrackId.HasValue)
            .OrderBy(static status => status.TrackSourceId, StringComparer.OrdinalIgnoreCase)
            .Select(static status => new
            {
                sourceId = Normalize(status.TrackSourceId),
                localTrackId = status.LocalTrackId,
                identityStatus = Normalize(status.IdentityStatus)
            }));
        var payload = $"{candidateIdentityRevision}\n{localIdentityPayload}";
        return $"playlist-target-v1:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
