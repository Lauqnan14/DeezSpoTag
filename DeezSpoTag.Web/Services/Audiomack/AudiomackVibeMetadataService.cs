using System.Collections.Concurrent;
using System.Text.Json;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly double _minimumMatchConfidence;
    private readonly ConcurrentDictionary<string, (DateTimeOffset StoredAt, AudiomackVibeMetadata? Metadata)> _cache = new();

    public AudiomackVibeMetadataService(AudiomackApiClient apiClient, IHttpClientFactory httpClientFactory, double? minimumMatchConfidence = null)
    {
        _apiClient = apiClient;
        _httpClientFactory = httpClientFactory;
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

        var mapped = MapCandidate(best, Math.Round(bestScore, 3));
        return await EnrichFromNextDataAsync(mapped, best, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Public page enrichment: Audiomack's Next.js v13 payload carries the creator
    /// metadata anonymously. Only invoked when the signed candidate itself lacked
    /// subgenres/moods. Failure is always non-fatal.
    /// </summary>
    private async Task<AudiomackVibeMetadata?> EnrichFromNextDataAsync(
        AudiomackVibeMetadata mapped,
        AudiomackSongCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (mapped.Subgenres.Count > 0 && mapped.Moods.Count > 0)
        {
            return mapped;
        }

        var artistSlug = candidate.ArtistSlug ?? string.Empty;
        var songSlug = candidate.UrlSlug ?? string.Empty;
        if (artistSlug.Length == 0 || songSlug.Length == 0)
        {
            return mapped;
        }

        try
        {
            var debugPath = Environment.GetEnvironmentVariable("AUDIOMACK_DEBUG_PAYLOAD") == "1"
                ? "/tmp/deezspotag-audiomack-nextdata.json"
                : null;
            var song = await AudiomackNextDataExtractor.FetchSongObjectAsync(
                _httpClientFactory, artistSlug, songSlug, debugPath, cancellationToken).ConfigureAwait(false);
            if (song is null)
            {
                return mapped;
            }

            var subgenres = mapped.Subgenres.Count > 0
                ? mapped.Subgenres
                : ParseStringArray(song.Value, "subgenres");
            var typedTags = ParseTypedTags(song.Value);
            var moods = mapped.Moods.Count > 0
                ? mapped.Moods
                : typedTags is not null
                    ? typedTags.Where(tag => string.Equals(tag.Type, "mood", StringComparison.OrdinalIgnoreCase))
                        .Select(tag => tag.Name).ToList()
                    : ParseStringArray(song.Value, "moods");
            subgenres = subgenres.Count > 0 || typedTags is null
                ? subgenres
                : typedTags.Where(tag => string.Equals(tag.Type, "subgenre", StringComparison.OrdinalIgnoreCase))
                    .Select(tag => tag.Name).ToList();

            var parserVersion = "audiomack-nextjs-v13";
            return mapped with
            {
                Subgenres = subgenres,
                Moods = moods,
                RawTags = mapped.RawTags.Concat(ParseStringArray(song.Value, "tags"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                TrackId = mapped.TrackId ?? $"nextjs:{parserVersion}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return mapped;
        }
    }

    private static List<string> ParseStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!.Trim())
            .Where(item => item.Length > 0)
            .ToList();
    }

    private static List<(string Name, string? Type)>? ParseTypedTags(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var output = new List<(string Name, string? Type)>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()!.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var type = item.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()
                : null;
            output.Add((name, type));
        }

        return output.Count > 0 ? output : null;
    }
}
