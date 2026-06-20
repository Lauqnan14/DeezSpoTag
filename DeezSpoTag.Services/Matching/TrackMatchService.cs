using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Core.Models.Deezer;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Matching;

public sealed class TrackMatchService
{
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
            var isrcMatch = await TryMatchByIsrcAsync(identity);
            if (isrcMatch != null)
            {
                return isrcMatch;
            }

            // ISRC present but no match found: do not fallback to metadata search.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("No Deezer ISRC match for {Isrc}; skipping metadata fallback", identity.Isrc);
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(identity.Title) || string.IsNullOrWhiteSpace(identity.Artist))
        {
            return null;
        }

        return await TryMatchByMetadataAsync(identity);
    }

    private async Task<MatchResult?> TryMatchByIsrcAsync(TrackIdentity identity)
    {
        try
        {
            var track = await _deezerClient.GetTrackByIsrcAsync(identity.Isrc);
            if (!IsValidTrack(track))
            {
                return null;
            }

            var validation = ValidateCandidate(identity, track);
            if (!validation.Accepted)
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
                _logger.LogDebug(ex, "Deezer ISRC lookup failed for {Isrc}", identity.Isrc);
            }
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

                var validation = ValidateCandidate(identity, track);
                if (!validation.Accepted)
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
                _logger.LogDebug(ex, "Deezer metadata match failed for {Title} - {Artist}", identity.Title, identity.Artist);
            }
            return null;
        }
    }

    private static bool IsValidTrack(ApiTrack? track)
    {
        return track != null && !string.IsNullOrWhiteSpace(track.Id);
    }

    private static TrackCandidateValidationResult ValidateCandidate(
        TrackIdentity identity,
        ApiTrack track)
        => TrackCandidateValidator.Validate(
            new TrackMatchSource(
                identity.Isrc,
                identity.Title,
                identity.Artist,
                identity.Album,
                identity.DurationMs),
            new TrackMatchCandidate(
                track.Id,
                track.Isrc,
                BuildCandidateTitle(track),
                track.Artist?.Name,
                track.Album?.Title,
                track.Duration > 0 ? track.Duration * 1000 : null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 8_000));

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
