using System.Collections.Concurrent;
using DeezSpoTag.Core.Utils;

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
        return await EnrichFromNextDataAsync(mapped, best, artist, title, cancellationToken).ConfigureAwait(false);
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

        if (TrackTitleMatcher.HasVersionDrift(title, candidateTitle)
            || PageGuests(candidate).Except(RequestedGuests(artist, title), StringComparer.Ordinal).Any())
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
        foreach (var extra in candidate.Artists.Concat(SplitFeaturing(candidate.Featuring))
                     .Where(extra => extra.Length > 0 && artists.All(existing => !string.Equals(existing, extra, StringComparison.OrdinalIgnoreCase))))
        {
            artists.Add(extra);
        }

        var styles = AudiomackTaxonomy.CollectStyles(candidate);
        var moods = AudiomackTaxonomy.CollectMoods(candidate);
        var genres = AudiomackTaxonomy.CollectGenres(candidate, promoteStylesWhenEmpty: false);
        var rawTags = styles
            .Concat(moods)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AudiomackVibeMetadata
        {
            TrackId = candidate.Id,
            Url = candidate.Url,
            Title = candidate.Title?.Trim() ?? string.Empty,
            Artists = artists,
            PrimaryGenre = genres.Count > 0 ? genres[0] : null,
            Subgenres = styles,
            Moods = moods,
            RawTags = rawTags,
            MatchConfidence = Math.Clamp(matchConfidence, 0d, 1d)
        };
    }

    /// <summary>Guests the caller explicitly asked for, via the artist or title text.</summary>
    private static HashSet<string> RequestedGuests(string artist, string title)
    {
        var guests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in new[] { title, artist })
        {
            foreach (var guest in AudiomackIdNormalizer.ParseTitleFeaturedGuests(field))
            {
                guests.Add(guest);
            }
        }

        return guests;
    }

    /// <summary>Guests the page object explicitly credits.</summary>
    private static HashSet<string> PageGuests(AudiomackSongCandidate candidate)
    {
        var guests = new HashSet<string>(AudiomackIdNormalizer.ParseFeaturedGuests(candidate.Featuring), StringComparer.Ordinal);
        foreach (var field in new[] { candidate.Title, candidate.Artist }.Concat(candidate.Artists))
        {
            foreach (var guest in AudiomackIdNormalizer.ParseTitleFeaturedGuests(field))
            {
                guests.Add(guest);
            }
        }

        return guests;
    }

    private static IEnumerable<string> SplitFeaturing(string? featuring)
    {
        if (string.IsNullOrWhiteSpace(featuring))
        {
            yield break;
        }

        foreach (var part in featuring.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(part => part.Length > 0))
        {
            yield return part;
        }
    }

    private static string BuildArtistText(AudiomackSongCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Artist))
        {
            return candidate.Artist.Trim();
        }

        return string.Join(", ", candidate.Artists.Where(value => !string.IsNullOrWhiteSpace(value)));
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
        foreach (var index in new[] { "feat.", "featuring", "ft." }.Select(marker => lower.IndexOf(marker, StringComparison.Ordinal)))
        {
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
        string artist,
        string title,
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
            var debugPath = AudiomackDebugPayload.ResolveOutputPath();
            var song = await AudiomackNextDataExtractor.FetchSongObjectAsync(
                _httpClientFactory, candidate, debugPath, cancellationToken).ConfigureAwait(false);
            if (song is null)
            {
                return mapped;
            }

            var pageCandidate = AudiomackSongCandidate.FromJson(song.Value);
            if (pageCandidate is null)
            {
                return mapped;
            }

            // The page overlay must prove it is the same recording before it is
            // allowed to contribute anything. Title/version drift and a guest
            // credit the caller never asked for reject the overlay outright:
            // this consumer has no ISRC or duration evidence of its own.
            if (!AudiomackIdNormalizer.IsSameSong(candidate, pageCandidate, out _)
                || DeezSpoTag.Core.Utils.TrackTitleMatcher.HasVersionDrift(candidate.Title, pageCandidate.Title))
            {
                return mapped;
            }

            var requestedGuests = RequestedGuests(artist, title);
            var overlayGuests = PageGuests(pageCandidate);
            if (overlayGuests.Count > 0
                && !requestedGuests.SetEquals(overlayGuests)
                && (requestedGuests.Count == 0 || !overlayGuests.All(requestedGuests.Contains)))
            {
                return mapped;
            }

            if (!AudiomackTaxonomy.TryMerge(candidate, pageCandidate, out var merged, out _))
            {
                return mapped;
            }

            var finalScore = ScoreCandidate(merged, artist, title);
            if (finalScore < _minimumMatchConfidence)
            {
                return mapped;
            }

            var remapped = MapCandidate(merged, Math.Round(finalScore, 3));
            return remapped with
            {
                TrackId = remapped.TrackId ?? mapped.TrackId ?? "nextjs:audiomack-nextjs-v13"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return mapped;
        }
    }
}
