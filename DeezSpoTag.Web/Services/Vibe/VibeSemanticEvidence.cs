namespace DeezSpoTag.Web.Services.Vibe;

public enum VibeSemanticKind
{
    Genre,
    Style,
    Mood,
    Context,
    Unknown
}

public enum VibeEvidenceScope
{
    Track,
    Artist,
    Audio
}

/// <summary>
/// One typed piece of semantic evidence for Vibe resolution. Sources use stable
/// names: audiomack, lastfm, essentia-discogs519, essentia-mood.
/// </summary>
public sealed record VibeSemanticEvidence
{
    public required string Source { get; init; }

    public required VibeSemanticKind Kind { get; init; }

    public required VibeEvidenceScope Scope { get; init; }

    public required string RawValue { get; init; }

    public required string CanonicalValue { get; init; }

    public double Strength { get; init; }

    public double MatchConfidence { get; init; }

    public double FinalWeight { get; init; }
}

/// <summary>
/// Lightweight semantic normalizer. For Audiomack it only canonicalizes display
/// spelling — the field already declares the kind. For Last.fm it additionally
/// classifies untyped community tags; unknown tags stay Unknown (still evidence),
/// never forced into Genre/Mood.
/// </summary>
public static class VibeSemanticNormalizer
{
    private static readonly Dictionary<string, string> CanonicalAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["afro house"] = "Afro-House",
            ["afro-house"] = "Afro-House",
            ["afro pop"] = "Afropop",
            ["afro-pop"] = "Afropop",
            ["afropop"] = "Afropop",
            ["rnb"] = "R&B",
            ["r n b"] = "R&B",
            ["rhythm and blues"] = "R&B",
            ["hiphop"] = "Hip-Hop",
            ["hip hop"] = "Hip-Hop",
            ["hip-hop"] = "Hip-Hop"
        };

    private static readonly HashSet<string> MoodWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "happy", "sad", "party", "dance", "dancing", "energetic", "chill",
            "relaxed", "relaxing", "calm", "aggressive", "uplifting", "reflective",
            "romantic", "sleep", "sleepy", "mellow", "groovy", "upbeat", "melancholy",
            "emotional", "stargazing", "feel good", "chillout", "laid back"
        };

    public static string Canonicalize(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return CanonicalAliases.TryGetValue(trimmed, out var canonical)
            ? canonical
            : trimmed;
    }

    /// <summary>Classification for untyped Last.fm community tags.</summary>
    public static VibeSemanticKind ClassifyLastFmTag(string value)
    {
        var canonical = Canonicalize(value);
        if (canonical.Length == 0)
        {
            return VibeSemanticKind.Unknown;
        }

        if (MoodWords.Contains(canonical))
        {
            return VibeSemanticKind.Mood;
        }

        // Community tags are predominantly genre/style vocabulary; unknown values
        // remain Unknown evidence rather than being forced into a kind.
        return VibeSemanticKind.Unknown;
    }
}
