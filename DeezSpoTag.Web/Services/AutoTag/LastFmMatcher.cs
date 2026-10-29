using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LastFmMatcher
{
    private const int MaxCacheEntries = 10000;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex NonWordRegex = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled, RegexTimeout);
    private static readonly HashSet<string> JunkTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "seen live", "favorites", "favourites", "favorite", "albums i own", "songs i own",
        "male vocalists", "female vocalists", "under 2000 listeners"
    };
    private static readonly HashSet<string> MoodTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "happy", "sad", "melancholic", "melancholy", "romantic", "uplifting", "chill", "relaxing",
        "calm", "energetic", "aggressive", "dark", "dreamy", "mellow", "party", "emotional",
        "feel good", "fun", "beautiful", "chillout", "cheerful"
    };
    private static readonly HashSet<string> GenreTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "rock", "pop", "hip hop", "hip-hop", "rap", "r&b", "soul", "jazz", "blues", "reggae", "dancehall",
        "afrobeats", "afropop", "amapiano", "bongo flava", "highlife", "gospel", "country", "folk", "classical",
        "metal", "punk", "electronic", "electronica", "dance", "house", "techno", "trance", "disco", "funk",
        "latin", "world", "alternative", "indie"
    };
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PlatformAuthService _authService;
    private readonly ILogger<LastFmMatcher> _logger;

    public LastFmMatcher(IHttpClientFactory httpClientFactory, PlatformAuthService authService, ILogger<LastFmMatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, LastFmConfig config, CancellationToken cancellationToken)
    {
        var apiKey = (await _authService.LoadAsync()).LastFm?.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var artist = NormalizeDisplay(FirstNonEmpty(info.Artist, info.Artists.FirstOrDefault()));
        var title = NormalizeDisplay(OneTaggerMatching.CleanTitle(info.Title));
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;

        await ArtistAliasGateway.EnsureLoadedAsync(cancellationToken);
        var names = await ArtistAliasGateway.GetGroupNamesAsync(artist, cancellationToken);
        foreach (var lookupName in new[] { artist }.Concat(names).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = $"{NormalizeKey(lookupName)}:{NormalizeKey(title)}:{Math.Clamp(config.MaxTags, 1, 50)}:{Math.Max(0, config.MinTagCount)}:{NormalizeRelativeWeight(config.MinRelativeWeight, config.MinRelativeWeightUnit):R}";
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                var result = BuildResult(info, cached.Tags);
                if (result is not null) return result;
                continue;
            }

            var response = await FetchTrackTagsAsync(apiKey, lookupName, title, cancellationToken);
            if (response?.Error is 6 or 7)
            {
                _logger.LogInformation("Last.fm track lookup missed {Artist} - {Title}; checking remaining saved names.", lookupName, title);
                SetCache(cacheKey, Array.Empty<ClassifiedTag>(), NegativeCacheTtl);
                continue;
            }
            // Errors/outages must not cause requests under every saved alias.
            if (response is null || response.Error.HasValue) return null;
            if (!IdentityMatches(response.Toptags?.Attributes, lookupName, title))
            {
                _logger.LogInformation("Last.fm returned no verified recording identity for {Artist} - {Title}.", lookupName, title);
                continue;
            }

            var tags = ClassifyTags(response.Toptags!.Tag, config);
            if (tags.Length == 0)
            {
                // Retain the existing artist-tag path only after the track's identity is verified.
                var artistResponse = await FetchArtistTagsAsync(apiKey, lookupName, cancellationToken);
                if (artistResponse is null || artistResponse.Error is not (null or 6 or 7)) return null;
                if (!artistResponse.Error.HasValue && ArtistNamesMatch(artistResponse.Toptags?.Attributes?.Artist, lookupName))
                    tags = ClassifyTags(artistResponse.Toptags?.Tag, config);
            }

            SetCache(cacheKey, tags, tags.Length > 0 ? CacheTtl : NegativeCacheTtl);
            if (tags.Length > 0) return BuildResult(info, tags);
            _logger.LogInformation("Last.fm verified {Artist} - {Title}, but returned no tags passing the configured filters.", lookupName, title);
        }
        return null;
    }

    private async Task<LastFmTopTagsResponse?> FetchTrackTagsAsync(string apiKey, string artist, string title, CancellationToken token)
    {
        var url = $"https://ws.audioscrobbler.com/2.0/?method=track.gettoptags&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";
        return await FetchAsync(url, $"{artist} - {title}", token);
    }

    private async Task<LastFmTopTagsResponse?> FetchArtistTagsAsync(string apiKey, string artist, CancellationToken token)
    {
        var url = $"https://ws.audioscrobbler.com/2.0/?method=artist.gettoptags&artist={Uri.EscapeDataString(artist)}&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";
        return await FetchAsync(url, artist, token);
    }

    private async Task<LastFmTopTagsResponse?> FetchAsync(string url, string lookupContext, CancellationToken token)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await _httpClientFactory.CreateClient().GetFromJsonAsync<LastFmTopTagsResponse>(url, token);
                if (response?.Error is not (11 or 16 or 29) || attempt == 3) return response;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt == 3)
            {
                _logger.LogWarning(ex, "Last.fm AutoTag lookup failed for {LookupContext}", lookupContext);
                return null;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), token);
        }
        return null;
    }

    private static ClassifiedTag[] ClassifyTags(IEnumerable<LastFmTag>? rawTags, LastFmConfig config)
    {
        var weighted = rawTags?
            .Select(tag => new WeightedTag(NormalizeDisplay(tag.Name), tag.Count ?? 0))
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Name) && !JunkTags.Contains(tag.Name))
            .OrderByDescending(tag => tag.Count)
            .ToList() ?? new();
        var leading = weighted.FirstOrDefault()?.Count ?? 0;
        var minRelativeWeight = NormalizeRelativeWeight(config.MinRelativeWeight, config.MinRelativeWeightUnit);
        return weighted
            .Where(tag => tag.Count >= Math.Max(0, config.MinTagCount))
            .Where(tag => leading <= 0 || tag.Count / (double)leading >= minRelativeWeight)
            .Select(Classify)
            .Where(tag => tag != null)
            .Select(tag => tag!)
            .DistinctBy(tag => $"{tag.Kind}:{tag.Value}", StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(config.MaxTags, 1, 50))
            .ToArray();
    }

    /// <summary>
    /// The UI exposes the minimum tag share as a percentage (15 = 15%); the comparison needs a
    /// 0-1 fraction. Explicit percentages always divide by 100; unmarked profiles retain legacy fraction compatibility.
    /// </summary>
    private static double NormalizeRelativeWeight(double raw, string? unit)
    {
        var value = string.Equals(unit, "percent", StringComparison.OrdinalIgnoreCase) || raw > 1d
            ? raw / 100d : raw;
        return Math.Clamp(value, 0d, 1d);
    }

    private static bool IdentityMatches(LastFmTopTagsAttributes? attributes, string artist, string title)
    {
        if (attributes == null || string.IsNullOrWhiteSpace(attributes.Artist) || string.IsNullOrWhiteSpace(attributes.Track)) return false;
        return ArtistNamesMatch(attributes.Artist, artist)
            && !TrackTitleMatcher.HasVersionDrift(title, attributes.Track)
            && TrackTitleMatcher.HasCompatibleTitleIdentity(title, attributes.Track);
    }

    private static bool ArtistNamesMatch(string? actual, string? expected)
        => !string.IsNullOrWhiteSpace(actual) && !string.IsNullOrWhiteSpace(expected)
            && NormalizeKey(ArtistAliasGateway.ResolveCredit(actual)) == NormalizeKey(ArtistAliasGateway.ResolveCredit(expected));

    private static ClassifiedTag? Classify(WeightedTag tag)
    {
        if (MoodTags.Contains(tag.Name)) return new("mood", Canonicalize(tag.Name));
        if (GenreTags.Contains(tag.Name)) return new("genre", Canonicalize(tag.Name));
        if (tag.Name.Contains(" rock", StringComparison.OrdinalIgnoreCase)
            || tag.Name.Contains(" metal", StringComparison.OrdinalIgnoreCase)
            || tag.Name.Contains(" house", StringComparison.OrdinalIgnoreCase)
            || tag.Name.Contains(" pop", StringComparison.OrdinalIgnoreCase)) return new("style", Canonicalize(tag.Name));
        return null;
    }

    private static AutoTagMatchResult? BuildResult(AutoTagAudioInfo info, IReadOnlyList<ClassifiedTag> tags)
    {
        if (tags.Count == 0) return null;
        var artists = info.Artists.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(info.Artist)) artists.Add(info.Artist.Trim());
        return new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = new AutoTagTrack
            {
                Title = info.Title.Trim(), Artists = artists, AlbumArtists = artists.ToList(), Album = info.Album,
                Duration = info.DurationSeconds is > 0 ? TimeSpan.FromSeconds(info.DurationSeconds.Value) : null,
                Isrc = info.Isrc, TrackNumber = info.TrackNumber,
                Genres = tags.Where(tag => tag.Kind == "genre").Select(tag => tag.Value).ToList(),
                Styles = tags.Where(tag => tag.Kind == "style").Select(tag => tag.Value).ToList(),
                Mood = tags.FirstOrDefault(tag => tag.Kind == "mood")?.Value,
            }
        };
    }

    private void SetCache(string key, IReadOnlyList<ClassifiedTag> tags, TimeSpan ttl)
    {
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[key] = new(tags, DateTimeOffset.UtcNow.Add(ttl));
    }

    private static string NormalizeDisplay(string? value) => string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static string NormalizeKey(string? value) => NonWordRegex.Replace((value ?? string.Empty).ToLowerInvariant(), string.Empty);
    private static string Canonicalize(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    private sealed record WeightedTag(string Name, int Count);
    private sealed record ClassifiedTag(string Kind, string Value);
    private sealed record CacheEntry(IReadOnlyList<ClassifiedTag> Tags, DateTimeOffset ExpiresAt);
}
