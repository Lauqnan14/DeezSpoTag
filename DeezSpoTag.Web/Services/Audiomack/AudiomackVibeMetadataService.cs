using System.Collections.Concurrent;
using DeezSpoTag.Core.Utils;
using JsonException = System.Text.Json.JsonException;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Audiomack's creator/uploader metadata for one track, typed by the field it came
/// from. Audiomack is the primary online semantic authority for Vibe, so the field
/// type is authoritative: PrimaryGenre is genre evidence, Subgenres are style
/// evidence, Moods are mood evidence — even for values DeezSpoTag has never seen.
/// </summary>
public sealed record AudiomackVibeMetadata
{
    public string? TrackId { get; init; }
    public string? Url { get; init; }

    public string Title { get; init; } = "";
    public IReadOnlyList<string> Artists { get; init; } = Array.Empty<string>();

    public string? PrimaryGenre { get; init; }

    public IReadOnlyList<string> Subgenres { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Moods { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RawTags { get; init; } = Array.Empty<string>();

    public double MatchConfidence { get; init; }
}

public interface IAudiomackVibeMetadataService
{
    Task<AudiomackVibeMetadata?> FindTrackAsync(
        string artist,
        string title,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Confidence-gated Audiomack lookup for Vibe. Search results are never trusted
/// automatically: every candidate is normalized and scored against the requested
/// artist/title, and anything below the minimum confidence contributes no evidence.
/// Results are cached by track identity so repeated Vibe reads do not hit the network.
/// </summary>
public sealed class AudiomackVibeMetadataService : IAudiomackVibeMetadataService
{
    public const double DefaultMinimumMatchConfidence = 0.85;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly AudiomackApiClient _apiClient;
    private readonly double _minimumMatchConfidence;
    private readonly ConcurrentDictionary<string, (DateTimeOffset StoredAt, AudiomackVibeMetadata? Metadata)> _cache = new();

    public AudiomackVibeMetadataService(AudiomackApiClient apiClient, double? minimumMatchConfidence = null)
    {
        _apiClient = apiClient;
        _minimumMatchConfidence = minimumMatchConfidence ?? DefaultMinimumMatchConfidence;
    }

    public async Task<AudiomackVibeMetadata?> FindTrackAsync(
        string artist,
        string title,
        CancellationToken cancellationToken = default)
    {
        var normalizedArtist = (artist ?? string.Empty).Trim();
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (normalizedArtist.Length == 0 || normalizedTitle.Length == 0)
        {
            return null;
        }

        var cacheKey = $"{normalizedArtist.ToLowerInvariant()}|{normalizedTitle.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.StoredAt < CacheTtl)
        {
            return cached.Metadata;
        }

        var metadata = await FindTrackNoCacheAsync(normalizedArtist, normalizedTitle, cancellationToken)
            .ConfigureAwait(false);
        _cache[cacheKey] = (DateTimeOffset.UtcNow, metadata);
        return metadata;
    }

    private async Task<AudiomackVibeMetadata?> FindTrackNoCacheAsync(
        string artist,
        string title,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AudiomackSongCandidate> candidates;
        try
        {
            candidates = await _apiClient.SearchSongsAsync($"{artist} {title}", 10, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audiomack being unavailable must never fail Vibe; it only contributes
            // no semantic evidence.
            return null;
        }

        AudiomackSongCandidate? best = null;
        var bestScore = 0d;
        foreach (var candidate in candidates)
        {
            var score = ScoreCandidate(candidate, artist, title);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        if (best is null || bestScore < _minimumMatchConfidence)
        {
            return null;
        }

        return MapCandidate(best, Math.Round(bestScore, 3));
    }

    /// <summary>
    /// Normalized artist/title comparison. Featured artists are stripped from both
    /// sides before scoring; the score blends title and artist similarity equally.
    /// </summary>
    internal static double ScoreCandidate(AudiomackSongCandidate candidate, string artist, string title)
    {
        var candidateTitle = candidate.Title?.Trim() ?? string.Empty;
        var candidateArtists = BuildArtistText(candidate);
        if (candidateTitle.Length == 0 || candidateArtists.Length == 0)
        {
            return 0d;
        }

        var titleSimilarity = TextMatchUtils.ComputeNormalizedSimilarity(
            StripFeatures(title),
            StripFeatures(candidateTitle));
        var artistSimilarity = TextMatchUtils.ComputeNormalizedSimilarity(
            StripFeatures(artist),
            StripFeatures(candidateArtists));

        return Math.Clamp((titleSimilarity + artistSimilarity) / 2d, 0d, 1d);
    }

    internal static AudiomackVibeMetadata MapCandidate(AudiomackSongCandidate candidate, double matchConfidence)
    {
        var artists = candidate.Artist
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        var rawTags = candidate.Subgenres
            .Concat(candidate.Moods)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AudiomackVibeMetadata
        {
            TrackId = candidate.Id,
            Url = candidate.Url,
            Title = candidate.Title?.Trim() ?? string.Empty,
            Artists = artists,
            PrimaryGenre = string.IsNullOrWhiteSpace(candidate.Genre) ? null : candidate.Genre.Trim(),
            Subgenres = candidate.Subgenres,
            Moods = candidate.Moods,
            RawTags = rawTags,
            MatchConfidence = Math.Clamp(matchConfidence, 0d, 1d)
        };
    }

    private static string BuildArtistText(AudiomackSongCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Artist))
        {
            return candidate.Artist.Trim();
        }

        return candidate.UploaderName?.Trim() ?? string.Empty;
    }

    private static string StripFeatures(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var lower = trimmed.ToLowerInvariant();
        var cut = -1;
        foreach (var marker in new[] { "feat.", "featuring", "ft." })
        {
            var index = lower.IndexOf(marker, StringComparison.Ordinal);
            if (index > 0 && (char.IsWhiteSpace(lower[index - 1]) || lower[index - 1] == '('))
            {
                cut = index;
                break;
            }
        }

        if (cut > 0)
        {
            trimmed = trimmed[..cut].Trim().TrimEnd('(', '-').Trim();
        }

        return trimmed;
    }
}
