using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Pre-computes mood bucket assignments for fast mood mix generation.
/// Ported from Lidify's MoodBucketService with dual-mode scoring rules.
/// </summary>
public sealed class MoodBucketService
{
    private const string MoodHappyField = "MoodHappy";
    private const string MoodSadField = "MoodSad";
    private const string MoodRelaxedField = "MoodRelaxed";
    private const string MoodAggressiveField = "MoodAggressive";
    private const string MoodPartyField = "MoodParty";
    private const string MoodAcousticField = "MoodAcoustic";
    private const string MoodElectronicField = "MoodElectronic";
    private const string ValenceField = "Valence";
    private const string EnergyField = "Energy";
    private const string ArousalField = "Arousal";
    private const string BpmField = "Bpm";
    private const string DanceabilityField = "Danceability";
    private const string InstrumentalnessField = "Instrumentalness";
    private const string AcousticnessField = "Acousticness";
    private const string KeyScaleField = "KeyScale";

    private readonly LibraryRepository _repository;
    private readonly ILogger<MoodBucketService> _logger;

    public MoodBucketService(LibraryRepository repository, ILogger<MoodBucketService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public static IReadOnlyDictionary<string, MoodConfig> MoodConfigs { get; } = new Dictionary<string, MoodConfig>
    {
        ["happy"] = new("Happy & Upbeat",
            new MoodRule[] { new NumericRule(MoodHappyField, Min: 0.5), new NumericRule(MoodSadField, Max: 0.4) },
            new MoodRule[] { new NumericRule(ValenceField, Min: 0.6), new NumericRule(EnergyField, Min: 0.5) }),

        ["sad"] = new("Melancholic",
            new MoodRule[] { new NumericRule(MoodSadField, Min: 0.5), new NumericRule(MoodHappyField, Max: 0.4) },
            new MoodRule[] { new NumericRule(ValenceField, Max: 0.35), new StringRule(KeyScaleField, "minor") }),

        ["relaxed"] = new("Chill & Relaxed",
            new MoodRule[] { new NumericRule(MoodRelaxedField, Min: 0.5), new NumericRule(MoodAggressiveField, Max: 0.3) },
            new MoodRule[] { new NumericRule(EnergyField, Max: 0.5), new NumericRule(ArousalField, Max: 0.5) }),

        ["energetic"] = new("High Energy",
            new MoodRule[] { new NumericRule(ArousalField, Min: 0.6), new NumericRule(EnergyField, Min: 0.7) },
            new MoodRule[] { new NumericRule(BpmField, Min: 120), new NumericRule(EnergyField, Min: 0.7) }),

        ["party"] = new("Dance Party",
            new MoodRule[] { new NumericRule(MoodPartyField, Min: 0.5), new NumericRule(DanceabilityField, Min: 0.6) },
            new MoodRule[] { new NumericRule(DanceabilityField, Min: 0.7), new NumericRule(EnergyField, Min: 0.6) }),

        ["focus"] = new("Focus Mode",
            new MoodRule[] { new NumericRule(InstrumentalnessField, Min: 0.5), new NumericRule(MoodRelaxedField, Min: 0.3) },
            new MoodRule[] { new NumericRule(InstrumentalnessField, Min: 0.5), new NumericRule(EnergyField, Min: 0.2, Max: 0.6) }),

        ["melancholy"] = new("Deep Feels",
            new MoodRule[] { new NumericRule(MoodSadField, Min: 0.4), new NumericRule(ValenceField, Max: 0.4) },
            new MoodRule[] { new NumericRule(ValenceField, Max: 0.35), new StringRule(KeyScaleField, "minor") }),

        ["aggressive"] = new("Intense",
            new MoodRule[] { new NumericRule(MoodAggressiveField, Min: 0.5) },
            new MoodRule[] { new NumericRule(EnergyField, Min: 0.8), new NumericRule(ArousalField, Min: 0.7) }),

        ["acoustic"] = new("Acoustic Vibes",
            new MoodRule[] { new NumericRule(MoodAcousticField, Min: 0.5), new NumericRule(MoodElectronicField, Max: 0.4) },
            new MoodRule[] { new NumericRule(AcousticnessField, Min: 0.6), new NumericRule(EnergyField, Min: 0.3, Max: 0.6) }),
    };

    public static IReadOnlyList<string> ValidMoods { get; } = new List<string>(MoodConfigs.Keys);

    public async Task<IReadOnlyList<string>> AssignTrackToMoodsAsync(long trackId, CancellationToken cancellationToken = default)
    {
        var analysis = await _repository.GetTrackAnalysisAsync(trackId, cancellationToken);
        if (analysis is null || (analysis.Status != "complete" && analysis.Status != "completed"))
        {
            return Array.Empty<string>();
        }

        var scores = CalculateMoodScores(analysis);
        var assigned = new List<string>();

        foreach (var (mood, score) in scores)
        {
            // Persist zero scores as well so completed tracks are marked as evaluated.
            // Query paths that serve mixes and counts already filter on score >= 0.5.
            await _repository.UpsertMoodBucketAsync(trackId, mood, score, cancellationToken);
            if (score > 0)
            {
                assigned.Add(mood);
            }
        }

        _logger.LogDebug("Track {TrackId} assigned to moods: {Moods}", trackId,
            assigned.Count > 0 ? string.Join(", ", assigned) : "none");

        return assigned;
    }

    public static Dictionary<string, double> CalculateMoodScores(TrackAnalysisResultDto analysis)
    {
        var isEnhanced = string.Equals(analysis.AnalysisMode, "enhanced", StringComparison.OrdinalIgnoreCase);
        var scores = new Dictionary<string, double>();

        foreach (var (mood, config) in MoodConfigs)
        {
            var rules = isEnhanced ? config.PrimaryRules : config.FallbackRules;
            scores[mood] = EvaluateMoodRules(analysis, rules);
        }

        return scores;
    }

    private static double EvaluateMoodRules(TrackAnalysisResultDto analysis, IReadOnlyList<MoodRule> rules)
    {
        var totalScore = 0.0;
        var ruleCount = 0;

        foreach (var rule in rules)
        {
            switch (rule)
            {
                case StringRule sr:
                {
                    var value = GetStringField(analysis, sr.Field);
                    if (value is null) continue;
                    ruleCount++;
                    totalScore += string.Equals(value, sr.Expected, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    break;
                }
                case NumericRule nr:
                {
                    var value = GetNumericField(analysis, nr.Field);
                    if (!value.HasValue) continue;
                    ruleCount++;
                    totalScore += ScoreNumericConstraint(value.Value, nr.Min, nr.Max);
                    break;
                }
            }
        }

        if (ruleCount == 0) return 0;

        var avgScore = totalScore / ruleCount;
        return avgScore >= 0.5 ? avgScore : 0;
    }

    private static double ScoreNumericConstraint(double value, double? min, double? max)
    {
        if (min.HasValue && max.HasValue)
        {
            if (value >= min.Value && value <= max.Value) return 1;
            if (value < min.Value) return Math.Max(0, 1 - (min.Value - value) * 2);
            return Math.Max(0, 1 - (value - max.Value) * 2);
        }

        if (min.HasValue)
        {
            if (value >= min.Value) return Math.Min(1, 0.5 + (value - min.Value) * 0.5);
            return Math.Max(0, (value / min.Value) * 0.5);
        }

        if (max.HasValue)
        {
            if (value <= max.Value) return Math.Min(1, 0.5 + (max.Value - value) * 0.5);
            return Math.Max(0, ((1 - value) / (1 - max.Value)) * 0.5);
        }

        return 0;
    }

    private static double? GetNumericField(TrackAnalysisResultDto a, string field) => field switch
    {
        MoodHappyField => a.MoodHappy,
        MoodSadField => a.MoodSad,
        MoodRelaxedField => a.MoodRelaxed,
        MoodAggressiveField => a.MoodAggressive,
        MoodPartyField => a.MoodParty,
        MoodAcousticField => a.MoodAcoustic,
        MoodElectronicField => a.MoodElectronic,
        ValenceField => a.Valence,
        EnergyField => a.Energy,
        ArousalField => a.Arousal,
        BpmField => a.Bpm,
        DanceabilityField => a.DanceabilityMl ?? a.Danceability,
        InstrumentalnessField => a.Instrumentalness,
        AcousticnessField => a.Acousticness,
        _ => null,
    };

    private static string? GetStringField(TrackAnalysisResultDto a, string field) => field switch
    {
        KeyScaleField => a.KeyScale,
        _ => null,
    };

    public record MoodConfig(string Name, IReadOnlyList<MoodRule> PrimaryRules, IReadOnlyList<MoodRule> FallbackRules);
    public abstract record MoodRule(string Field);
    public record NumericRule(string Field, double? Min = null, double? Max = null) : MoodRule(Field);
    public record StringRule(string Field, string Expected) : MoodRule(Field);
}
