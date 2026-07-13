using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

internal static class VibeSimilarityScorer
{
    internal static double CalculateSimilarity(
        TrackAnalysisResultDto source,
        TrackAnalysisResultDto candidate)
        => CosineSimilarity(BuildFeatureVector(source), BuildFeatureVector(candidate));

    internal static double CalculateMatchScore(
        TrackAnalysisResultDto source,
        TrackAnalysisResultDto candidate)
        => CalculateSimilarity(source, candidate) * 0.95 + ComputeTagBonus(source, candidate);

    internal static bool HasMeaningfulFeatureCoverage(TrackAnalysisResultDto analysis)
    {
        var populatedFeatureCount = new double?[]
        {
            analysis.MoodHappy,
            analysis.MoodSad,
            analysis.MoodRelaxed,
            analysis.MoodAggressive,
            analysis.MoodParty,
            analysis.MoodAcoustic,
            analysis.MoodElectronic,
            analysis.Energy,
            analysis.ValenceMl ?? analysis.Valence,
            analysis.ArousalMl ?? analysis.Arousal,
            analysis.DanceabilityMl ?? analysis.Danceability,
            analysis.Instrumentalness,
            analysis.Acousticness,
            analysis.Speechiness,
            analysis.Bpm
        }.Count(static value => value.HasValue);
        return populatedFeatureCount >= 6;
    }

    private static double[] BuildFeatureVector(TrackAnalysisResultDto track)
    {
        var isOod = DetectOod(track);

        double GetMoodValue(double? value, double defaultValue)
        {
            if (!value.HasValue)
            {
                return defaultValue;
            }

            return isOod
                ? 0.2 + Math.Max(0, Math.Min(0.6, value.Value - 0.2))
                : value.Value;
        }

        return
        [
            GetMoodValue(track.MoodHappy, 0.5) * 1.3,
            GetMoodValue(track.MoodSad, 0.5) * 1.3,
            GetMoodValue(track.MoodRelaxed, 0.5) * 1.3,
            GetMoodValue(track.MoodAggressive, 0.5) * 1.3,
            GetMoodValue(track.MoodParty, 0.5) * 1.3,
            GetMoodValue(track.MoodAcoustic, 0.5) * 1.3,
            GetMoodValue(track.MoodElectronic, 0.5) * 1.3,
            track.Energy ?? 0.5,
            CalculateEnhancedArousal(track),
            track.DanceabilityMl ?? track.Danceability ?? 0.5,
            track.Instrumentalness ?? 0.5,
            1 - OctaveAwareBpmDistance(track.Bpm ?? 120, 120),
            CalculateEnhancedValence(track)
        ];
    }

    private static bool DetectOod(TrackAnalysisResultDto track)
    {
        var coreMoods = new[]
        {
            track.MoodHappy ?? 0.5,
            track.MoodSad ?? 0.5,
            track.MoodRelaxed ?? 0.5,
            track.MoodAggressive ?? 0.5
        };
        var minMood = coreMoods.Min();
        var maxMood = coreMoods.Max();
        var allHigh = minMood > 0.7 && maxMood - minMood < 0.3;
        var allNeutral = Math.Abs(maxMood - 0.5) < 0.15 && Math.Abs(minMood - 0.5) < 0.15;
        return allHigh || allNeutral;
    }

    private static double CalculateEnhancedValence(TrackAnalysisResultDto track)
    {
        var happy = track.MoodHappy ?? 0.5;
        var sad = track.MoodSad ?? 0.5;
        var party = track.MoodParty ?? 0.5;
        var modeValence = string.Equals(track.KeyScale, "major", StringComparison.OrdinalIgnoreCase)
            ? 0.3
            : string.Equals(track.KeyScale, "minor", StringComparison.OrdinalIgnoreCase)
                ? -0.2
                : 0;
        var moodValence = happy * 0.35 + party * 0.25 + (1 - sad) * 0.2;
        var audioValence = (track.Energy ?? 0.5) * 0.1
            + (track.DanceabilityMl ?? track.Danceability ?? 0.5) * 0.1;
        return Math.Clamp(moodValence + modeValence + audioValence, 0, 1);
    }

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

    private static double OctaveAwareBpmDistance(double firstBpm, double secondBpm)
    {
        if (firstBpm <= 0 || secondBpm <= 0)
        {
            return 0;
        }

        var first = NormalizeToOctave(firstBpm);
        var second = NormalizeToOctave(secondBpm);
        return Math.Min(Math.Abs(Math.Log2(first) - Math.Log2(second)), 1);
    }

    private static double NormalizeToOctave(double bpm)
    {
        while (bpm < 77)
        {
            bpm *= 2;
        }

        while (bpm > 154)
        {
            bpm /= 2;
        }

        return bpm;
    }

    private static double CosineSimilarity(double[] left, double[] right)
    {
        var dot = 0d;
        var leftMagnitude = 0d;
        var rightMagnitude = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        return leftMagnitude <= double.Epsilon || rightMagnitude <= double.Epsilon
            ? 0
            : dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static double ComputeTagBonus(
        TrackAnalysisResultDto source,
        TrackAnalysisResultDto candidate)
    {
        var sourceTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTags(sourceTags, source.LastfmTags);
        AddTags(sourceTags, source.EssentiaGenres);
        var candidateTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTags(candidateTags, candidate.LastfmTags);
        AddTags(candidateTags, candidate.EssentiaGenres);
        if (sourceTags.Count == 0 || candidateTags.Count == 0)
        {
            return 0;
        }

        var overlap = sourceTags.Count(candidateTags.Contains);
        return Math.Min(0.05, overlap * 0.01);
    }

    private static void AddTags(HashSet<string> destination, IReadOnlyList<string>? tags)
    {
        if (tags is null)
        {
            return;
        }

        foreach (var tag in tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)))
        {
            destination.Add(tag);
        }
    }
}
