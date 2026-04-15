using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Matching;

public sealed class TrackMatchService
{
    private const int DurationToleranceMs = 2000;
    private const int SearchLimit = 10;
    private readonly DeezerClient _deezerClient;
    private readonly ILogger<TrackMatchService> _logger;

    public TrackMatchService(DeezerClient deezerClient, ILogger<TrackMatchService> logger)
    {
        _deezerClient = deezerClient;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchDeezerAsync(TrackIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!string.IsNullOrWhiteSpace(identity.Isrc))
        {
            var isrcMatch = await TryMatchByIsrcAsync(identity.Isrc);
            if (isrcMatch != null)
            {
                return isrcMatch;
            }

            // ISRC present but no match found: do not fallback to metadata search.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("No Deezer ISRC match for {Isrc}; skipping metadata fallback", identity.Isrc);            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(identity.Title) || string.IsNullOrWhiteSpace(identity.Artist))
        {
            return null;
        }

        return await TryMatchByMetadataAsync(identity);
    }

    private async Task<MatchResult?> TryMatchByIsrcAsync(string isrc)
    {
        try
        {
            var track = await _deezerClient.GetTrackByIsrcAsync(isrc);
            if (!IsValidTrack(track))
            {
                return null;
            }

            return new MatchResult
            {
                Provider = "deezer",
                ProviderTrackId = track.Id,
                Confidence = MatchConfidence.ExactIsrc,
                Reason = "ISRC exact match"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer ISRC lookup failed for {Isrc}", isrc);            }
            return null;
        }
    }

    private async Task<MatchResult?> TryMatchByMetadataAsync(TrackIdentity identity)
    {
        var query = $"{identity.Artist} {identity.Title}";
        try
        {
            var results = await _deezerClient.SearchTrackAsync(query, new ApiOptions
            {
                Limit = SearchLimit,
                Strict = true
            });

            if (results.Data == null)
            {
                return null;
            }

            ApiTrack? best = null;
            foreach (var candidate in results.Data)
            {
                if (candidate is not ApiTrack track)
                {
                    continue;
                }

                if (!IsValidTrack(track))
                {
                    continue;
                }

                if (!IsTitleArtistMatch(identity, track))
                {
                    continue;
                }

                if (!IsDurationMatch(identity.DurationMs, track.Duration))
                {
                    continue;
                }

                best = track;
                break;
            }

            if (best == null)
            {
                return null;
            }

            return new MatchResult
            {
                Provider = "deezer",
                ProviderTrackId = best.Id,
                Confidence = MatchConfidence.High,
                Reason = "Metadata match (title/artist/duration)"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer metadata match failed for {Title} - {Artist}", identity.Title, identity.Artist);            }
            return null;
        }
    }

    private static bool IsValidTrack(ApiTrack? track)
    {
        return track != null && !string.IsNullOrWhiteSpace(track.Id);
    }

    private static bool IsTitleArtistMatch(TrackIdentity identity, ApiTrack track)
    {
        var candidateTitle = BuildCandidateTitle(track);
        return TrackTitleMatcher.TitlesMatch(identity.Title, candidateTitle)
            && TrackTitleMatcher.ArtistsMatch(identity.Artist, track.Artist?.Name ?? "");
    }

    private static bool IsDurationMatch(int? durationMs, int durationSeconds)
    {
        if (!durationMs.HasValue || durationSeconds <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - (durationSeconds * 1000));
        return delta <= DurationToleranceMs;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string BuildCandidateTitle(ApiTrack track)
    {
        var title = Normalize(track.Title);
        var titleVersion = Normalize(track.TitleVersion);
        if (string.IsNullOrWhiteSpace(titleVersion) || title.Contains(titleVersion, StringComparison.Ordinal))
        {
            return track.Title;
        }

        return $"{track.Title} {track.TitleVersion}".Trim();
    }
}
