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
        var sourceVector = BuildFeatureVector(sourceAnalysis);

        var scored = candidates
            .Select(candidate =>
            {
                var targetVector = BuildFeatureVector(candidate);
                var similarity = CosineSimilarity(sourceVector, targetVector);
                var tagBonus = ComputeTagBonus(sourceAnalysis, candidate);
                var finalScore = similarity * 0.95 + tagBonus;

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

    // --- 13-dimensional feature vector (ported from Lidify) ---

    private static double[] BuildFeatureVector(TrackAnalysisResultDto track)
    {
        var isOod = DetectOod(track);

        double GetMoodValue(double? value, double defaultValue)
        {
            if (!value.HasValue) return defaultValue;
            if (!isOod) return value.Value;
            // Normalize OOD predictions to spread them out (0.2-0.8 range)
            return 0.2 + Math.Max(0, Math.Min(0.6, value.Value - 0.2));
        }

        var enhancedValence = CalculateEnhancedValence(track);
        var enhancedArousal = CalculateEnhancedArousal(track);

        return new[]
        {
            // ML Mood predictions (7 features) - 1.3x semantic weight
            GetMoodValue(track.MoodHappy, 0.5) * 1.3,
            GetMoodValue(track.MoodSad, 0.5) * 1.3,
            GetMoodValue(track.MoodRelaxed, 0.5) * 1.3,
            GetMoodValue(track.MoodAggressive, 0.5) * 1.3,
            GetMoodValue(track.MoodParty, 0.5) * 1.3,
            GetMoodValue(track.MoodAcoustic, 0.5) * 1.3,
            GetMoodValue(track.MoodElectronic, 0.5) * 1.3,
            // Audio features (5 features)
            track.Energy ?? 0.5,
            enhancedArousal,
            track.DanceabilityMl ?? track.Danceability ?? 0.5,
            track.Instrumentalness ?? 0.5,
            // Octave-aware BPM normalized to 0-1 (similarity to 120 BPM reference)
            1 - OctaveAwareBpmDistance(track.Bpm ?? 120, 120),
            // Enhanced valence
            enhancedValence,
        };
    }

    // --- OOD detection (out-of-distribution for classical/ambient/piano) ---

    private static bool DetectOod(TrackAnalysisResultDto track)
    {
        var coreMoods = new[]
        {
            track.MoodHappy ?? 0.5,
            track.MoodSad ?? 0.5,
            track.MoodRelaxed ?? 0.5,
            track.MoodAggressive ?? 0.5,
        };

        var minMood = coreMoods.Min();
        var maxMood = coreMoods.Max();

        // Flag if all core moods are high (>0.7) with low variance
        var allHigh = minMood > 0.7 && (maxMood - minMood) < 0.3;
        // Or if all are very neutral (~0.5)
        var allNeutral = Math.Abs(maxMood - 0.5) < 0.15 && Math.Abs(minMood - 0.5) < 0.15;

        return allHigh || allNeutral;
    }

    // --- Enhanced valence: mode + mood + audio features ---

    private static double CalculateEnhancedValence(TrackAnalysisResultDto track)
    {
        var happy = track.MoodHappy ?? 0.5;
        var sad = track.MoodSad ?? 0.5;
        var party = track.MoodParty ?? 0.5;

        var isMajor = string.Equals(track.KeyScale, "major", StringComparison.OrdinalIgnoreCase);
        var isMinor = string.Equals(track.KeyScale, "minor", StringComparison.OrdinalIgnoreCase);
        var modeValence = 0d;
        if (isMajor)
        {
            modeValence = 0.3;
        }
        else if (isMinor)
        {
            modeValence = -0.2;
        }

        var moodValence = happy * 0.35 + party * 0.25 + (1 - sad) * 0.2;
        var audioValence = (track.Energy ?? 0.5) * 0.1
            + (track.DanceabilityMl ?? track.Danceability ?? 0.5) * 0.1;

        return Math.Clamp(moodValence + modeValence + audioValence, 0, 1);
    }

    // --- Enhanced arousal: mood + energy + tempo (avoids unreliable electronic mood) ---

    private static double CalculateEnhancedArousal(TrackAnalysisResultDto track)
    {
        var aggressive = track.MoodAggressive ?? 0.5;
        var party = track.MoodParty ?? 0.5;
        var relaxed = track.MoodRelaxed ?? 0.5;
        var acoustic = track.MoodAcoustic ?? 0.5;
        var energy = track.Energy ?? 0.5;
        var bpm = track.Bpm ?? 120;

        var moodArousal = aggressive * 0.3 + party * 0.2;
        var energyArousal = energy * 0.25;
        var tempoArousal = Math.Clamp((bpm - 60) / 120, 0, 1) * 0.15;
        var calmReduction = (1 - relaxed) * 0.05 + (1 - acoustic) * 0.05;

        return Math.Clamp(moodArousal + energyArousal + tempoArousal + calmReduction, 0, 1);
    }

    // --- Octave-aware BPM distance (60 ≈ 120 ≈ 240 BPM) ---

    private static double NormalizeToOctave(double bpm)
    {
        while (bpm < 77) bpm *= 2;
        while (bpm > 154) bpm /= 2;
        return bpm;
    }

    private static double OctaveAwareBpmDistance(double bpm1, double bpm2)
    {
        if (bpm1 <= 0 || bpm2 <= 0) return 0;
        var norm1 = NormalizeToOctave(bpm1);
        var norm2 = NormalizeToOctave(bpm2);
        var logDistance = Math.Abs(Math.Log2(norm1) - Math.Log2(norm2));
        return Math.Min(logDistance, 1.0);
    }

    // --- Cosine similarity ---

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA <= double.Epsilon || magB <= double.Epsilon) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    // --- Tag/genre overlap bonus (max 5%) ---

    private static double ComputeTagBonus(TrackAnalysisResultDto source, TrackAnalysisResultDto candidate)
    {
        var sourceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTags(sourceSet, source.LastfmTags);
        AddTags(sourceSet, source.EssentiaGenres);

        var trackSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTags(trackSet, candidate.LastfmTags);
        AddTags(trackSet, candidate.EssentiaGenres);

        if (sourceSet.Count == 0 || trackSet.Count == 0) return 0;

        var overlap = sourceSet.Count(tag => trackSet.Contains(tag));
        return Math.Min(0.05, overlap * 0.01);
    }

    private static void AddTags(HashSet<string> set, IReadOnlyList<string>? tags)
    {
        if (tags is null)
        {
            return;
        }

        foreach (var tag in tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)))
        {
            set.Add(tag);
        }
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
