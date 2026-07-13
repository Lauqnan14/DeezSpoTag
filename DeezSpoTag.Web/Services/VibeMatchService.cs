using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class VibeMatchService
{
    private readonly LibraryRepository _repository;
    private readonly LastFmTagService _lastFmTagService;

    public VibeMatchService(LibraryRepository repository, LastFmTagService lastFmTagService)
    {
        _repository = repository;
        _lastFmTagService = lastFmTagService;
    }

    public async Task<VibeMatchResponseDto> GetMatchesAsync(long trackId, int limit, CancellationToken cancellationToken)
    {
        var sourceAnalysis = await _repository.GetTrackAnalysisAsync(trackId, cancellationToken);
        var sourceSummaries = await _repository.GetTrackSummariesAsync(new[] { trackId }, cancellationToken);
        var sourceSummary = sourceSummaries.Count > 0 ? sourceSummaries[0] : null;

        if (sourceAnalysis is null || limit <= 0)
        {
            return new VibeMatchResponseDto(
                trackId,
                sourceSummary?.Title,
                sourceSummary?.ArtistName,
                sourceAnalysis,
                Array.Empty<VibeMatchTrackDto>());
        }

        var candidateLimit = Math.Clamp(limit * 10, 50, 500);
        var candidates = await _repository.GetTrackAnalysisCandidatesAsync(
            sourceAnalysis.LibraryId,
            trackId,
            candidateLimit,
            cancellationToken);

        var isEnhanced = string.Equals(sourceAnalysis.AnalysisMode, "enhanced", StringComparison.OrdinalIgnoreCase);
        var scored = candidates
            .Select(candidate =>
            {
                var finalScore = VibeSimilarityScorer.CalculateMatchScore(sourceAnalysis, candidate);

                return new
                {
                    candidate.TrackId,
                    candidate.AnalysisMode,
                    candidate.Energy,
                    candidate.Bpm,
                    candidate.Valence,
                    candidate.Arousal,
                    candidate.MoodTags,
                    candidate.Danceability,
                    Score = finalScore
                };
            })
            .Where(item =>
            {
                // Lower threshold for enhanced mode (more precise features)
                var minThreshold = isEnhanced ? 0.4 : 0.5;
                return item.Score > minThreshold;
            })
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .ToList();

        var selectedIds = new HashSet<long>(scored.Select(item => item.TrackId));
        if (selectedIds.Count < limit)
        {
            await AddFallbackTracksAsync(trackId, sourceSummary, selectedIds, limit, cancellationToken);
        }

        if (selectedIds.Count == 0)
        {
            return new VibeMatchResponseDto(
                trackId,
                sourceSummary?.Title,
                sourceSummary?.ArtistName,
                sourceAnalysis,
                Array.Empty<VibeMatchTrackDto>());
        }

        var orderedIds = scored.Select(item => item.TrackId)
            .Concat(selectedIds.Where(id => scored.All(entry => entry.TrackId != id)))
            .Take(limit)
            .ToList();

        var summaries = await _repository.GetTrackSummariesAsync(orderedIds, cancellationToken);
        var analysisMap = await _repository.GetTrackAnalysisByTrackIdsAsync(orderedIds, cancellationToken);
        var summaryMap = summaries.ToDictionary(item => item.TrackId);

        var matches = new List<VibeMatchTrackDto>();
        foreach (var trackIdItem in orderedIds)
        {
            if (!summaryMap.TryGetValue(trackIdItem, out var summary))
            {
                continue;
            }

            analysisMap.TryGetValue(trackIdItem, out var analysis);
            var score = scored.FirstOrDefault(item => item.TrackId == trackIdItem)?.Score ?? 0;

            matches.Add(new VibeMatchTrackDto(
                summary.TrackId,
                summary.Title,
                summary.ArtistName,
                summary.AlbumTitle,
                summary.CoverPath,
                summary.DurationMs,
                Math.Round(score, 4),
                analysis?.AnalysisMode,
                analysis?.Energy,
                analysis?.Bpm,
                analysis?.Valence,
                analysis?.Arousal,
                analysis?.Danceability,
                analysis?.MoodTags));
        }

        return new VibeMatchResponseDto(
            trackId,
            sourceSummary?.Title,
            sourceSummary?.ArtistName,
            sourceAnalysis,
            matches);
    }

    // --- Fallback matching ---

    private async Task AddFallbackTracksAsync(
        long trackId,
        MixTrackDto? sourceSummary,
        HashSet<long> selected,
        int limit,
        CancellationToken cancellationToken)
    {
        if (selected.Count >= limit)
        {
            return;
        }

        await AddSameArtistFallbackAsync(trackId, selected, limit, cancellationToken);
        if (selected.Count >= limit)
        {
            return;
        }

        await AddSimilarArtistFallbackAsync(trackId, sourceSummary, selected, limit, cancellationToken);
        if (selected.Count >= limit)
        {
            return;
        }

        await AddSameGenreFallbackAsync(trackId, selected, limit, cancellationToken);
        if (selected.Count >= limit)
        {
            return;
        }

        await AddRandomFallbackAsync(trackId, selected, limit, cancellationToken);
    }

    private async Task AddSameArtistFallbackAsync(
        long trackId,
        HashSet<long> selected,
        int limit,
        CancellationToken cancellationToken)
    {
        var remaining = limit - selected.Count;
        if (remaining <= 0)
        {
            return;
        }

        var artistId = await _repository.GetArtistIdForTrackAsync(trackId, cancellationToken);
        if (!artistId.HasValue)
        {
            return;
        }

        var sameArtist = await _repository.GetTrackIdsByArtistAsync(artistId.Value, trackId, remaining, cancellationToken);
        AddTracksUntilLimit(selected, sameArtist, limit);
    }

    private async Task AddSimilarArtistFallbackAsync(
        long trackId,
        MixTrackDto? sourceSummary,
        HashSet<long> selected,
        int limit,
        CancellationToken cancellationToken)
    {
        var remaining = limit - selected.Count;
        if (remaining <= 0 || sourceSummary is null)
        {
            return;
        }

        var similarArtists = await _lastFmTagService.GetSimilarArtistsAsync(sourceSummary.ArtistName, 10, cancellationToken);
        if (similarArtists is null || similarArtists.Count == 0)
        {
            return;
        }

        var similarArtistTracks = await _repository.FindTrackIdsByArtistNamesAsync(
            similarArtists,
            trackId,
            remaining,
            cancellationToken);
        AddTracksUntilLimit(selected, similarArtistTracks, limit);
    }

    private async Task AddSameGenreFallbackAsync(
        long trackId,
        HashSet<long> selected,
        int limit,
        CancellationToken cancellationToken)
    {
        var remaining = limit - selected.Count;
        if (remaining <= 0)
        {
            return;
        }

        var genres = await _repository.GetGenresForTrackAsync(trackId, cancellationToken);
        if (genres.Count == 0)
        {
            return;
        }

        var sameGenre = await _repository.GetTrackIdsByGenresAsync(genres, trackId, remaining, cancellationToken);
        AddTracksUntilLimit(selected, sameGenre, limit);
    }

    private async Task AddRandomFallbackAsync(
        long trackId,
        HashSet<long> selected,
        int limit,
        CancellationToken cancellationToken)
    {
        var remaining = limit - selected.Count;
        if (remaining <= 0)
        {
            return;
        }

        var random = await _repository.GetRandomAnalyzedTrackIdsAsync(trackId, remaining, cancellationToken);
        AddTracksUntilLimit(selected, random, limit);
    }

    private static void AddTracksUntilLimit(HashSet<long> selected, IEnumerable<long> trackIds, int limit)
    {
        foreach (var id in trackIds)
        {
            selected.Add(id);
            if (selected.Count >= limit)
            {
                return;
            }
        }
    }
}
