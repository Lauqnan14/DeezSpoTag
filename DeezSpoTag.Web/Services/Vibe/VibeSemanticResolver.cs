using DeezSpoTag.Web.Services.Audiomack;

namespace DeezSpoTag.Web.Services.Vibe;

/// <summary>
/// Source-aware Vibe resolution. Priority per semantic kind:
/// Audiomack track metadata → Last.fm track tags → acoustic (Discogs519 /
/// Essentia moods) → Last.fm artist fallback. Priority is not destructive
/// replacement: every independent piece of evidence stays visible.
/// </summary>
public static class VibeSemanticResolver
{
    public const double AudiomackWeight = 1.0;
    public const double AcousticWeight = 0.60;

    public sealed record AcousticGenreEvidence(string Label, double Score, string Model);

    public sealed record AcousticMoodEvidence(string Name, double Score);

    public sealed record VibeResolution(
        IReadOnlyList<string> ResolvedGenres,
        IReadOnlyList<string> ResolvedStyles,
        IReadOnlyList<string> ResolvedMoods,
        IReadOnlyList<VibeSemanticEvidence> SemanticEvidence);

    public static VibeResolution Resolve(
        AudiomackVibeMetadata? audiomack,
        IReadOnlyList<LastFmTagService.LastFmTagEvidence>? lastFmTrackTags,
        IReadOnlyList<LastFmTagService.LastFmTagEvidence>? lastFmArtistTags,
        IReadOnlyList<AcousticGenreEvidence>? acousticGenres,
        IReadOnlyList<AcousticMoodEvidence>? acousticMoods)
    {
        var evidence = new List<VibeSemanticEvidence>();

        // Audiomack: creator-declared, typed by field.
        if (audiomack is not null)
        {
            if (!string.IsNullOrWhiteSpace(audiomack.PrimaryGenre))
            {
                evidence.Add(Build(
                    "audiomack", VibeSemanticKind.Genre, VibeEvidenceScope.Track,
                    audiomack.PrimaryGenre!, AudiomackWeight, audiomack.MatchConfidence));
            }

            foreach (var subgenre in audiomack.Subgenres)
            {
                evidence.Add(Build(
                    "audiomack", VibeSemanticKind.Style, VibeEvidenceScope.Track,
                    subgenre, AudiomackWeight, audiomack.MatchConfidence));
            }

            foreach (var mood in audiomack.Moods)
            {
                evidence.Add(Build(
                    "audiomack", VibeSemanticKind.Mood, VibeEvidenceScope.Track,
                    mood, AudiomackWeight, audiomack.MatchConfidence));
            }
        }

        // Last.fm: untyped community tags, classified conservatively.
        AppendLastFm(evidence, lastFmTrackTags, VibeEvidenceScope.Track, LastFmTagService.VibeTrackTagWeight);
        AppendLastFm(evidence, lastFmArtistTags, VibeEvidenceScope.Artist, LastFmTagService.VibeArtistTagWeight);

        // Acoustic: Discogs519 hierarchy split (broad genre / style) + Essentia moods.
        if (acousticGenres is not null)
        {
            foreach (var acoustic in acousticGenres)
            {
                var separator = acoustic.Label.IndexOf("---", StringComparison.Ordinal);
                var broad = separator > 0 ? acoustic.Label[..separator].Trim() : acoustic.Label.Trim();
                var style = separator > 0 ? acoustic.Label[(separator + 3)..].Trim() : string.Empty;

                if (broad.Length > 0)
                {
                    evidence.Add(Build(
                        "essentia-discogs519", VibeSemanticKind.Genre, VibeEvidenceScope.Audio,
                        broad, AcousticWeight * acoustic.Score, canonicalRaw: acoustic.Label));
                }

                if (style.Length > 0)
                {
                    evidence.Add(Build(
                        "essentia-discogs519", VibeSemanticKind.Style, VibeEvidenceScope.Audio,
                        style, AcousticWeight * acoustic.Score, canonicalRaw: acoustic.Label));
                }
            }
        }

        if (acousticMoods is not null)
        {
            foreach (var mood in acousticMoods)
            {
                evidence.Add(Build(
                    "essentia-mood", VibeSemanticKind.Mood, VibeEvidenceScope.Audio,
                    mood.Name, AcousticWeight * mood.Score));
            }
        }

        return new VibeResolution(
            ResolveKind(evidence, VibeSemanticKind.Genre),
            ResolveKind(evidence, VibeSemanticKind.Style),
            ResolveKind(evidence, VibeSemanticKind.Mood),
            evidence);
    }

    private static VibeSemanticEvidence Build(
        string source,
        VibeSemanticKind kind,
        VibeEvidenceScope scope,
        string rawValue,
        double weight,
        double matchConfidence = 1d,
        string? canonicalRaw = null)
    {
        var canonical = VibeSemanticNormalizer.Canonicalize(rawValue);
        return new VibeSemanticEvidence
        {
            Source = source,
            Kind = kind,
            Scope = scope,
            RawValue = canonicalRaw ?? rawValue,
            CanonicalValue = canonical,
            Strength = Math.Round(weight, 3),
            MatchConfidence = Math.Round(matchConfidence, 3),
            FinalWeight = Math.Round(weight * matchConfidence, 3)
        };
    }

    private static void AppendLastFm(
        List<VibeSemanticEvidence> evidence,
        IReadOnlyList<LastFmTagService.LastFmTagEvidence>? tags,
        VibeEvidenceScope scope,
        double authorityMultiplier)
    {
        if (tags is null)
        {
            return;
        }

        foreach (var tag in tags)
        {
            if (tag.RelativeWeight < LastFmTagService.VibeRelativeWeightFloor)
            {
                continue;
            }

            var kind = VibeSemanticNormalizer.ClassifyLastFmTag(tag.Name);
            evidence.Add(Build(
                "lastfm",
                kind == VibeSemanticKind.Unknown ? VibeSemanticKind.Style : kind,
                scope,
                tag.Name,
                authorityMultiplier * tag.RelativeWeight));
        }
    }

    private static IReadOnlyList<string> ResolveKind(
        IReadOnlyList<VibeSemanticEvidence> evidence,
        VibeSemanticKind kind)
    {
        var candidates = evidence
            .Where(item => item.Kind == kind && item.CanonicalValue.Length > 0)
            .GroupBy(item => item.CanonicalValue, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new
            {
                Value = group.Key,
                FirstAppearance = index,
                Best = group.OrderByDescending(item => RankSource(item)).First(),
                FinalWeight = group.Sum(item => item.FinalWeight)
            })
            .ToList();
        if (candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        // The winning tier is the highest source rank present (Audiomack →
        // Last.fm track → acoustic → Last.fm artist). Lower tiers stay in evidence.
        var winningRank = candidates.Max(item => RankSource(item.Best));
        return candidates
            .Where(item => RankSource(item.Best) == winningRank)
            .OrderBy(item => item.FirstAppearance)
            .Select(item => item.Value)
            .ToList();
    }

    /// <summary>Rank for resolution: higher wins. Audiomack → Last.fm track →
    /// acoustic → Last.fm artist fallback.</summary>
    private static int RankSource(VibeSemanticEvidence evidence)
        => evidence.Source switch
        {
            "audiomack" => 3,
            "lastfm" => evidence.Scope == VibeEvidenceScope.Track ? 2 : 0,
            _ => 1
        };
}
