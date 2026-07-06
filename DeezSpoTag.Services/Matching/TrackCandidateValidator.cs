using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Services.Matching;

public sealed record TrackMatchSource(
    string? Isrc,
    string? Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    int? ReleaseYear = null);

public sealed record TrackMatchCandidate(
    string? ProviderId,
    string? Isrc,
    string? Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    int? ReleaseYear = null);

public sealed record TrackCandidateValidationOptions(
    bool StrictWithoutIsrc = true,
    bool AllowMissingCandidateArtist = false,
    bool RequireCandidateDurationWhenSourceHasDuration = false,
    int MaxIsrcDurationDifferenceMs = 20_000,
    int MaxMetadataDurationDifferenceMs = 8_000);

public sealed record TrackCandidateValidationResult(
    bool Accepted,
    string Reason,
    double Score);

public static class TrackCandidateValidator
{
    public static TrackCandidateValidationResult Validate(
        TrackMatchSource source,
        TrackMatchCandidate candidate,
        TrackCandidateValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        options ??= new TrackCandidateValidationOptions();

        if (string.IsNullOrWhiteSpace(candidate.ProviderId))
        {
            return Reject("missing_candidate_id");
        }

        var sourceIsrc = NormalizeIsrc(source.Isrc);
        var candidateIsrc = NormalizeIsrc(candidate.Isrc);
        if (!string.IsNullOrWhiteSpace(sourceIsrc)
            && !string.IsNullOrWhiteSpace(candidateIsrc)
            && !string.Equals(sourceIsrc, candidateIsrc, StringComparison.OrdinalIgnoreCase))
        {
            return Reject("isrc_mismatch");
        }

        if (!string.IsNullOrWhiteSpace(sourceIsrc)
            && string.Equals(sourceIsrc, candidateIsrc, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateExactIsrcCandidate(source, candidate, options);
        }

        return ValidateMetadataCandidate(source, candidate, options);
    }

    public static string NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("-", string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 12 && normalized.All(char.IsLetterOrDigit)
            ? normalized
            : string.Empty;
    }

    private static TrackCandidateValidationResult ValidateExactIsrcCandidate(
        TrackMatchSource source,
        TrackMatchCandidate candidate,
        TrackCandidateValidationOptions options)
    {
        if (HasTitle(source) && HasTitle(candidate) && !TrackTitleMatcher.TitlesMatch(source.Title, candidate.Title))
        {
            return Reject("title_mismatch");
        }

        if (HasArtist(source) && HasArtist(candidate) && !TrackTitleMatcher.ArtistsMatch(source.Artist, candidate.Artist))
        {
            return Reject("artist_mismatch");
        }

        var durationResult = ValidateDuration(
            source.DurationMs,
            candidate.DurationMs,
            options.MaxIsrcDurationDifferenceMs,
            requireCandidateDuration: false);
        if (durationResult != null)
        {
            return durationResult;
        }

        if (HasAlbum(source) && HasAlbum(candidate) && !TrackTitleMatcher.TitlesMatch(source.Album, candidate.Album))
        {
            return Reject("album_mismatch");
        }

        return Accept("isrc", 1.0d);
    }

    private static TrackCandidateValidationResult ValidateMetadataCandidate(
        TrackMatchSource source,
        TrackMatchCandidate candidate,
        TrackCandidateValidationOptions options)
    {
        if (!HasTitle(source) || !HasTitle(candidate))
        {
            return Reject("missing_title");
        }

        if (!TrackTitleMatcher.TitlesMatch(source.Title, candidate.Title))
        {
            return Reject("title_mismatch");
        }

        var artistResult = ValidateArtist(source, candidate, options);
        if (artistResult != null)
        {
            return artistResult;
        }

        var durationResult = ValidateDuration(
            source.DurationMs,
            candidate.DurationMs,
            options.MaxMetadataDurationDifferenceMs,
            options.RequireCandidateDurationWhenSourceHasDuration);
        if (durationResult != null)
        {
            return durationResult;
        }

        if (HasAlbum(source) && HasAlbum(candidate) && !TrackTitleMatcher.TitlesMatch(source.Album, candidate.Album))
        {
            return Reject("album_mismatch");
        }

        if (source.ReleaseYear.HasValue
            && candidate.ReleaseYear.HasValue
            && Math.Abs(source.ReleaseYear.Value - candidate.ReleaseYear.Value) > 1)
        {
            return Reject("release_year_mismatch");
        }

        return Accept("metadata", ComputeMetadataScore(source, candidate));
    }

    private static TrackCandidateValidationResult? ValidateArtist(
        TrackMatchSource source,
        TrackMatchCandidate candidate,
        TrackCandidateValidationOptions options)
    {
        if (!HasArtist(source))
        {
            return null;
        }

        if (!HasArtist(candidate))
        {
            return options.AllowMissingCandidateArtist ? null : Reject("missing_candidate_artist");
        }

        var artistsMatch = options.StrictWithoutIsrc
            ? TrackTitleMatcher.StrictArtistsMatch(source.Artist, candidate.Artist)
            : TrackTitleMatcher.ArtistsMatch(source.Artist, candidate.Artist);
        return artistsMatch ? null : Reject("artist_mismatch");
    }

    private static TrackCandidateValidationResult? ValidateDuration(
        int? expectedMs,
        int? candidateMs,
        int toleranceMs,
        bool requireCandidateDuration)
    {
        if (expectedMs is not > 0)
        {
            return null;
        }

        if (candidateMs is not > 0)
        {
            return requireCandidateDuration ? Reject("missing_candidate_duration") : null;
        }

        return Math.Abs(expectedMs.Value - candidateMs.Value) > toleranceMs
            ? Reject("duration_mismatch")
            : null;
    }

    private static double ComputeMetadataScore(TrackMatchSource source, TrackMatchCandidate candidate)
    {
        var score = 0.55d;
        if (HasArtist(source) && HasArtist(candidate))
        {
            score += 0.2d;
        }
        if (source.DurationMs is > 0 && candidate.DurationMs is > 0)
        {
            score += 0.15d;
        }
        if (HasAlbum(source) && HasAlbum(candidate))
        {
            score += 0.1d;
        }
        return Math.Min(1.0d, score);
    }

    private static bool HasTitle(TrackMatchSource source) => !string.IsNullOrWhiteSpace(source.Title);

    private static bool HasTitle(TrackMatchCandidate candidate) => !string.IsNullOrWhiteSpace(candidate.Title);

    private static bool HasArtist(TrackMatchSource source) => !string.IsNullOrWhiteSpace(source.Artist);

    private static bool HasArtist(TrackMatchCandidate candidate) => !string.IsNullOrWhiteSpace(candidate.Artist);

    private static bool HasAlbum(TrackMatchSource source) => !string.IsNullOrWhiteSpace(source.Album);

    private static bool HasAlbum(TrackMatchCandidate candidate) => !string.IsNullOrWhiteSpace(candidate.Album);

    private static TrackCandidateValidationResult Accept(string reason, double score)
        => new(true, reason, score);

    private static TrackCandidateValidationResult Reject(string reason)
        => new(false, reason, 0d);
}
