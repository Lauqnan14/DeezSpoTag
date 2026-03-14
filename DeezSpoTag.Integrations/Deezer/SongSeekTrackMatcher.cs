using System.Text.RegularExpressions;

namespace DeezSpoTag.Integrations.Deezer;

internal static class SongSeekTrackMatcher
{
    internal const double PerfectThreshold = 0.7;
    internal const double PartialThreshold = 0.1;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private const string OriginalVersionType = "original";
    private const string LiveVersionType = "live";
    private const string RemasterVersionType = "remaster";
    private const string AlternateVersionType = "version";
    private const string DeluxeVersionType = "deluxe";

    internal readonly record struct TrackIdentity(string Title, string Artist, string? Album, int? DurationSeconds);
    internal readonly record struct Candidate(string Title, string Artist, string? Album, int? DurationSeconds, string Id);

    internal readonly record struct MatchResult(
        double Score,
        SongSeekMatchType MatchType,
        double TitleScore,
        double ArtistScore,
        double DurationScore,
        double AlbumScore,
        bool StrippedMatch);

    internal enum SongSeekMatchType
    {
        None,
        Partial,
        Perfect
    }

    private static readonly Regex RemoveParenBracket = CreateRegex(@"\s*\(.*?\)|\[.*?\]", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumeric = CreateRegex(@"[^a-z0-9\s]", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespace = CreateRegex(@"\s+", RegexOptions.Compiled);
    private static readonly Regex AlbumNoise = CreateRegex(@"\b(remaster(ed)?|deluxe|greatest hits|expanded|edition|version|bonus|explicit|clean|single|ep|album|live|session|sessions|anniversary|reissue|original|platinum|collection|hits|box set|disc|cd|vinyl|digital|mono|stereo)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistNoise = CreateRegex(@"\b(feat|ft|featuring|with|vs|and|&)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YearToken = CreateRegex(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);

    private static readonly Regex RemasterRegex = CreateRegex(@"remaster(ed)?|remasterizado|remastered", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LiveRegex = CreateRegex(@"live|concert|performance|at\s+\w+|recorded\s+live", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionRegex = CreateRegex(@"version|mix|edit|radio|extended|short|instrumental|acoustic|unplugged", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeluxeRegex = CreateRegex(@"deluxe|greatest hits|expanded|edition|bonus|explicit|clean|single|ep|album|session|sessions|anniversary|reissue|original|platinum|collection|hits|box set|disc|cd|vinyl|digital|mono|stereo",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RemasterCombined = CreateRegex(@"remaster(ed)?(?:\s+\d{4})?|\d{4}\s+remaster(ed)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LiveCombined = CreateRegex(@"live(?:\s+\d{4})?|\d{4}\s+live|concert(?:\s+\d{4})?|\d{4}\s+concert", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionCombined = CreateRegex(@"(version|mix|edit|radio|extended|short|instrumental|acoustic|unplugged)(?:\s+\d{4})?|\d{4}\s+(version|mix|edit|radio|extended|short|instrumental|acoustic|unplugged)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeluxeCombined = CreateRegex(@"(deluxe|greatest hits|expanded|edition|bonus|explicit|clean|single|ep|album|session|sessions|anniversary|reissue|original|platinum|collection|hits|box set|disc|cd|vinyl|digital|mono|stereo)(?:\s+\d{4})?|\d{4}\s+(deluxe|greatest hits|expanded|edition|bonus|explicit|clean|single|ep|album|session|sessions|anniversary|reissue|original|platinum|collection|hits|box set|disc|cd|vinyl|digital|mono|stereo)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);

    public static MatchResult ScoreTrackMatch(TrackIdentity input, Candidate candidate)
    {
        var firstAttempt = ScoreTrackMatchInternal(input, candidate, isStrippedVersion: false);

        MatchResult? secondAttempt = null;
        if (firstAttempt.MatchType is SongSeekMatchType.None or SongSeekMatchType.Partial)
        {
            var strippedTitle = GetBaseTitle(input.Title);
            var strippedCandidateTitle = GetBaseTitle(candidate.Title);
            if (!string.Equals(strippedTitle, Normalize(input.Title), StringComparison.Ordinal)
                || !string.Equals(strippedCandidateTitle, Normalize(candidate.Title), StringComparison.Ordinal))
            {
                var strippedInput = input with { Title = strippedTitle };
                var strippedCandidate = candidate with { Title = strippedCandidateTitle };
                secondAttempt = ScoreTrackMatchInternal(strippedInput, strippedCandidate, isStrippedVersion: true);
            }
        }

        var finalResult = firstAttempt;
        if (secondAttempt.HasValue && secondAttempt.Value.Score > firstAttempt.Score)
        {
            finalResult = secondAttempt.Value with
            {
                MatchType = SongSeekMatchType.Partial,
                StrippedMatch = true
            };
        }

        finalResult = finalResult with { MatchType = DetermineMatchType(finalResult.Score) };

        return finalResult;
    }

    private static MatchResult ScoreTrackMatchInternal(TrackIdentity input, Candidate candidate, bool isStrippedVersion)
    {
        var inputTitle = Normalize(input.Title);
        var inputArtist = NormalizeArtist(input.Artist);
        var inputAlbum = NormalizeAlbum(input.Album ?? string.Empty);
        var compTitle = Normalize(candidate.Title);
        var compArtist = NormalizeArtist(candidate.Artist);
        var compAlbum = NormalizeAlbum(candidate.Album ?? string.Empty);

        var inputVersion = GetVersionType(input.Title);
        var compVersion = GetVersionType(candidate.Title);

        var versionCompatibilityMultiplier = 1.0;
        var versionRejected = false;

        if (inputVersion != compVersion && !isStrippedVersion)
        {
            (versionRejected, versionCompatibilityMultiplier) = ResolveVersionCompatibility(inputVersion, compVersion);
        }

        var titleScore = TokenSetRatio(inputTitle, compTitle) / 100d;
        titleScore *= versionCompatibilityMultiplier;
        titleScore = Math.Clamp(titleScore, 0d, 1d);

        var artistScore = CalculateArtistScore(inputArtist, compArtist, candidate.Artist);
        var albumScore = TokenSetRatio(inputAlbum, compAlbum) / 100d;
        var durationScore = CalculateDurationScore(input.DurationSeconds, candidate.DurationSeconds);

        var isAppleMusicConversion = string.IsNullOrWhiteSpace(input.Album)
                                     || input.Title.Contains(" - ", StringComparison.Ordinal)
                                     || input.Artist.Contains(" - ", StringComparison.Ordinal);

        var totalScore = CalculateTotalScore(isAppleMusicConversion, titleScore, durationScore, artistScore, albumScore);

        if (versionRejected)
        {
            return new MatchResult(0d, SongSeekMatchType.None, 0d, artistScore, durationScore, albumScore, false);
        }

        if (isStrippedVersion)
        {
            return new MatchResult(totalScore, SongSeekMatchType.Partial, titleScore, artistScore, durationScore, albumScore, true);
        }

        var matchType = ResolveMatchType(titleScore, artistScore);
        return new MatchResult(totalScore, matchType, titleScore, artistScore, durationScore, albumScore, false);
    }

    private static double CalculateArtistScore(string inputArtist, string compArtist, string? candidateArtist)
    {
        var artistScore = TokenSetRatio(inputArtist, compArtist) / 100d;
        if (!string.IsNullOrWhiteSpace(inputArtist) && !string.IsNullOrWhiteSpace(compArtist))
        {
            var inputTokens = new HashSet<string>(inputArtist.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var compTokens = new HashSet<string>(compArtist.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var intersectionCount = inputTokens.Intersect(compTokens).Count();
            var unionCount = inputTokens.Union(compTokens).Count();
            var jaccard = unionCount > 0 ? (double)intersectionCount / unionCount : 0d;
            artistScore = Math.Max(artistScore, jaccard);
        }

        return string.IsNullOrWhiteSpace(candidateArtist)
            ? artistScore * 0.7
            : artistScore;
    }

    private static double CalculateDurationScore(int? inputDurationSeconds, int? candidateDurationSeconds)
    {
        if (!inputDurationSeconds.HasValue || !candidateDurationSeconds.HasValue)
        {
            return 0.8;
        }

        var diff = Math.Abs(inputDurationSeconds.Value - candidateDurationSeconds.Value);
        return diff <= 2 ? 1d : 1d - Math.Min(10, diff) / 10d;
    }

    private static double CalculateTotalScore(bool isAppleMusicConversion, double titleScore, double durationScore, double artistScore, double albumScore)
    {
        return isAppleMusicConversion
            ? (0.6 * titleScore) + (0.25 * durationScore) + (0.15 * artistScore)
            : (0.5 * titleScore) + (0.3 * durationScore) + (0.15 * artistScore) + (0.05 * albumScore);
    }

    private static SongSeekMatchType ResolveMatchType(double titleScore, double artistScore)
    {
        if (titleScore >= 0.6 && artistScore >= 0.3)
        {
            return SongSeekMatchType.Perfect;
        }

        if ((titleScore >= 0.4 && artistScore >= 0.2) || titleScore >= 0.7)
        {
            return SongSeekMatchType.Partial;
        }

        return SongSeekMatchType.None;
    }

    private static (bool Rejected, double Multiplier) ResolveVersionCompatibility(string inputVersion, string compVersion)
    {
        if (RequiresExactVersionMatch(inputVersion) && inputVersion != compVersion)
        {
            return (true, 1.0);
        }

        if (inputVersion == DeluxeVersionType && compVersion != DeluxeVersionType)
        {
            return (false, 0.6);
        }

        if (inputVersion == OriginalVersionType)
        {
            return (false, 0.7);
        }

        if (compVersion == OriginalVersionType)
        {
            return (false, 0.3);
        }

        return (false, 0.5);
    }

    private static bool RequiresExactVersionMatch(string versionType)
    {
        return versionType == LiveVersionType
            || versionType == RemasterVersionType
            || versionType == AlternateVersionType;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = RemoveParenBracket.Replace(normalized, string.Empty);
        normalized = NonAlphaNumeric.Replace(normalized, string.Empty);
        normalized = CollapseWhitespace.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static SongSeekMatchType DetermineMatchType(double score)
    {
        if (score >= PerfectThreshold)
        {
            return SongSeekMatchType.Perfect;
        }

        if (score >= PartialThreshold)
        {
            return SongSeekMatchType.Partial;
        }

        return SongSeekMatchType.None;
    }

    private static string NormalizeAlbum(string value)
    {
        var normalized = Normalize(value);
        normalized = AlbumNoise.Replace(normalized, string.Empty);
        normalized = YearToken.Replace(normalized, string.Empty);
        normalized = CollapseWhitespace.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static string NormalizeArtist(string value)
    {
        var normalized = Normalize(value);
        normalized = ArtistNoise.Replace(normalized, string.Empty);
        normalized = CollapseWhitespace.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static string GetVersionType(string value)
    {
        var lower = value.ToLowerInvariant();

        if (RemasterCombined.IsMatch(lower))
        {
            return "remaster";
        }
        if (LiveCombined.IsMatch(lower))
        {
            return "live";
        }
        if (VersionCombined.IsMatch(lower))
        {
            return "version";
        }
        if (DeluxeCombined.IsMatch(lower))
        {
            return "deluxe";
        }
        if (RemasterRegex.IsMatch(lower))
        {
            return "remaster";
        }
        if (LiveRegex.IsMatch(lower))
        {
            return "live";
        }
        if (VersionRegex.IsMatch(lower))
        {
            return "version";
        }
        if (YearToken.IsMatch(lower))
        {
            return "year";
        }
        if (DeluxeRegex.IsMatch(lower))
        {
            return "deluxe";
        }

        return "original";
    }

    private static string GetBaseTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = RemoveParenBracket.Replace(normalized, string.Empty);
        normalized = AlbumNoise.Replace(normalized, string.Empty);
        normalized = YearToken.Replace(normalized, string.Empty);
        normalized = NonAlphaNumeric.Replace(normalized, string.Empty);
        normalized = CollapseWhitespace.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static int TokenSetRatio(string source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        var sourceTokens = new HashSet<string>(source.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var candidateTokens = new HashSet<string>(candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (sourceTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0;
        }

        var intersection = sourceTokens.Intersect(candidateTokens).OrderBy(token => token).ToList();
        var diffSource = sourceTokens.Except(candidateTokens).OrderBy(token => token).ToList();
        var diffCandidate = candidateTokens.Except(sourceTokens).OrderBy(token => token).ToList();

        var sortedIntersection = string.Join(" ", intersection);
        var combinedSource = string.Join(" ", intersection.Concat(diffSource));
        var combinedCandidate = string.Join(" ", intersection.Concat(diffCandidate));

        var ratio1 = Ratio(sortedIntersection, combinedSource);
        var ratio2 = Ratio(sortedIntersection, combinedCandidate);
        var ratio3 = Ratio(combinedSource, combinedCandidate);

        return Math.Max(ratio1, Math.Max(ratio2, ratio3));
    }

    private static int Ratio(string a, string b)
    {
        return DeezerStringSimilarity.Ratio(a, b);
    }
}
