using System.Globalization;
using DeezSpoTag.Web.Services.Audiomack;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// AutoTag matcher for Audiomack. Anonymous by design: requests reuse the
/// app's existing self-healing two-legged OAuth signing (see
/// <see cref="AudiomackApiClient"/>) — no user credentials, no PlatformAuthService
/// entry. Matching is id/URL-first (embedded AUDIOMACK id/url tags, mirroring
/// Boomplay's match_by_id) and falls back to text search scored through the shared
/// OneTagger matching core. Only metadata Audiomack reliably provides is written;
/// the descriptor intentionally does not claim ISRC, BPM or key.
/// </summary>
public sealed class AudiomackMatcher
{
    private static readonly string[] TrackIdTagKeys =
    {
        "AUDIOMACK_TRACK_ID",
        "AUDIOMACK_ID",
        "AUDIOMACKID"
    };

    private static readonly string[] UrlTagKeys =
    {
        "URL",
        "WWW",
        "SOURCEURL",
        "SOURCE_URL",
        "AUDIOMACK_URL"
    };

    private readonly AudiomackApiClient _apiClient;
    private readonly ILogger<AudiomackMatcher> _logger;

    public AudiomackMatcher(AudiomackApiClient apiClient, ILogger<AudiomackMatcher> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        AudiomackMatchConfig config,
        CancellationToken cancellationToken)
    {
        var resolvedConfig = NormalizeConfig(config);
        if (resolvedConfig.MatchById)
        {
            var byId = await TryMatchByIdAsync(info, resolvedConfig, cancellationToken).ConfigureAwait(false);
            if (byId != null)
            {
                return byId;
            }
        }

        return await TryMatchBySearchAsync(info, matchingConfig, resolvedConfig, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutoTagMatchResult?> TryMatchByIdAsync(
        AutoTagAudioInfo info,
        AudiomackMatchConfig config,
        CancellationToken cancellationToken)
    {
        // URL-first: an embedded Audiomack URL carries the artist/song slugs.
        foreach (var url in ReadTagValues(info, UrlTagKeys))
        {
            if (!AudiomackIdNormalizer.TryExtractSongSlugs(url, out var artistSlug, out var songSlug))
            {
                continue;
            }

            var song = await _apiClient.GetSongAsync(artistSlug, songSlug, cancellationToken).ConfigureAwait(false);
            if (song == null)
            {
                continue;
            }

            var hydrated = await HydrateSelectedAsync(song, cancellationToken, alreadyFetchedSong: true).ConfigureAwait(false);
            if (AudiomackTaxonomy.IsNonMusicCandidate(hydrated))
            {
                continue;
            }

            var result = ToMatchResult(hydrated, accuracy: 1.0d, "id", info);
            if (result != null)
            {
                return result;
            }
        }

        // Id-only fallback: search by the tagged identity to re-resolve the song.
        foreach (var trackId in ReadTagValues(info, TrackIdTagKeys))
        {
            var candidates = await _apiClient.SearchSongsAsync(trackId, config.SearchLimit, cancellationToken).ConfigureAwait(false);
            var exact = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, trackId, StringComparison.OrdinalIgnoreCase)
                && !AudiomackTaxonomy.IsNonMusicCandidate(candidate));
            if (exact == null)
            {
                continue;
            }

            var hydrated = await HydrateSelectedAsync(exact, cancellationToken).ConfigureAwait(false);
            if (AudiomackTaxonomy.IsNonMusicCandidate(hydrated))
            {
                continue;
            }

            var result = ToMatchResult(hydrated, accuracy: 1.0d, "id", info);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> TryMatchBySearchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig matchingConfig,
        AudiomackMatchConfig config,
        CancellationToken cancellationToken)
    {
        var artist = OneTaggerMatching.CleanArtistSearching(
            info.Artists.Count > 0 ? string.Join(" ", info.Artists) : info.Artist);
        var query = $"{artist} {OneTaggerMatching.CleanTitle(info.Title)}".Trim();
        if (query.Length == 0)
        {
            return null;
        }

        var candidates = await _apiClient
            .SearchSongsAsync(query, Math.Max(config.SearchLimit, 5), cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        var usable = candidates
            .Where(candidate => !AudiomackTaxonomy.IsNonMusicCandidate(candidate))
            .Select(candidate => (Candidate: candidate, Track: ToAutoTagTrack(candidate)))
            .Where(pair => pair.Track != null)
            .Select(pair => (pair.Candidate, Track: pair.Track!))
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        var match = OneTaggerMatching.MatchTrack(
            info,
            usable.Select(pair => pair.Track).ToList(),
            matchingConfig,
            new OneTaggerMatching.TrackSelectors<AutoTagTrack>(
                track => track.Title,
                _ => null,
                track => track.Artists.Count > 0 ? track.Artists : track.AlbumArtists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);
        if (match == null)
        {
            return null;
        }

        var selected = usable.First(pair => ReferenceEquals(pair.Track, match.Track));
        var hydrated = await HydrateSelectedAsync(selected.Candidate, cancellationToken).ConfigureAwait(false);
        if (AudiomackTaxonomy.IsNonMusicCandidate(hydrated))
        {
            return null;
        }

        var track = ToAutoTagTrack(hydrated) ?? match.Track;
        return new AutoTagMatchResult
        {
            Accuracy = match.Accuracy,
            Track = track,
            MatchStrategy = "text"
        };
    }

    private static AutoTagMatchResult? ToMatchResult(
        AudiomackSongCandidate song,
        double accuracy,
        string strategy,
        AutoTagAudioInfo info)
    {
        var track = ToAutoTagTrack(song);
        if (track == null)
        {
            return null;
        }

        // Identity fields the provider could not supply fall back to the local file.
        if (string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(info.Title))
        {
            track.Title = info.Title;
        }

        return new AutoTagMatchResult
        {
            Accuracy = accuracy,
            Track = track,
            MatchStrategy = strategy
        };
    }

    internal static AutoTagTrack? ToAutoTagTrack(AudiomackSongCandidate song)
    {
        if (string.IsNullOrWhiteSpace(song.Title))
        {
            return null;
        }

        var artists = CollectArtists(song);
        var genres = AudiomackTaxonomy.CollectGenres(song, promoteStylesWhenEmpty: true, titleCase: true);
        var styles = AudiomackTaxonomy.CollectStyles(song, titleCase: true);
        var moods = AudiomackTaxonomy.CollectMoods(song, titleCase: true);
        var track = new AutoTagTrack
        {
            Title = song.Title.Trim(),
            Artists = artists,
            Album = string.IsNullOrWhiteSpace(song.Album) ? null : song.Album.Trim(),
            Duration = song.DurationSeconds is > 0
                ? TimeSpan.FromSeconds(song.DurationSeconds.Value)
                : null,
            Genres = genres.ToList(),
            Styles = styles.ToList(),
            Mood = moods.Count == 0 ? null : string.Join(", ", moods),
            Label = string.IsNullOrWhiteSpace(song.Label) ? null : song.Label.Trim(),
            Isrc = string.IsNullOrWhiteSpace(song.Isrc) ? null : song.Isrc.Trim(),
            Art = song.ArtworkUrl,
            Url = song.Url,
            TrackId = song.Id,
            ReleaseId = song.AlbumId,
            AlbumId = song.AlbumId
        };

        if (!string.IsNullOrWhiteSpace(song.Album) && track.Artists.Count > 0)
        {
            track.AlbumArtists = new List<string>(track.Artists);
        }

        if (!string.IsNullOrWhiteSpace(song.ReleasedDate))
        {
            track.ReleaseDate = ParseReleasedDate(song.ReleasedDate);
        }

        return track;
    }

    private async Task<AudiomackSongCandidate> HydrateSelectedAsync(
        AudiomackSongCandidate song,
        CancellationToken cancellationToken,
        bool alreadyFetchedSong = false)
    {
        var current = song;
        if (!TryResolveSlugs(current, out var artistSlug, out var songSlug))
        {
            return current;
        }

        if (!alreadyFetchedSong)
        {
            try
            {
                var detailed = await _apiClient.GetSongAsync(artistSlug, songSlug, cancellationToken).ConfigureAwait(false);
                if (detailed != null)
                {
                    current = AudiomackTaxonomy.Merge(current, detailed);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Audiomack selected song hydration failed ({ArtistSlug}/{SongSlug})", artistSlug, songSlug);
            }
        }

        if (AudiomackTaxonomy.HasPageTaxonomy(current)
            && AudiomackTaxonomy.CollectMoods(current).Count > 0)
        {
            return current;
        }

        try
        {
            var page = await _apiClient.GetPublicPageSongAsync(artistSlug, songSlug, cancellationToken).ConfigureAwait(false);
            if (page != null)
            {
                current = AudiomackTaxonomy.Merge(current, page);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Audiomack public page hydration failed ({ArtistSlug}/{SongSlug})", artistSlug, songSlug);
        }

        return current;
    }

    private static bool TryResolveSlugs(
        AudiomackSongCandidate song,
        out string artistSlug,
        out string songSlug)
    {
        artistSlug = song.ArtistSlug?.Trim() ?? string.Empty;
        songSlug = song.UrlSlug?.Trim() ?? string.Empty;
        if (artistSlug.Length > 0 && songSlug.Length > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(song.Url)
            && AudiomackIdNormalizer.TryExtractSongSlugs(song.Url, out var fromUrlArtist, out var fromUrlSong))
        {
            artistSlug = fromUrlArtist;
            songSlug = fromUrlSong;
            return true;
        }

        artistSlug = string.Empty;
        songSlug = string.Empty;
        return false;
    }

    private static List<string> CollectArtists(AudiomackSongCandidate song)
    {
        var artists = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length == 0
                    || artists.Any(existing => string.Equals(existing, part, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                artists.Add(part);
            }
        }

        Add(song.Artist);
        foreach (var value in song.Artists)
        {
            Add(value);
        }

        Add(song.Featuring);
        if (artists.Count == 0)
        {
            Add(song.UploaderName);
        }

        return artists;
    }

    private static DateTime? ParseReleasedDate(string raw)
    {
        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var released))
        {
            return released;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) || unix <= 0)
        {
            return null;
        }

        try
        {
            return unix > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadTagValues(AutoTagAudioInfo info, string[] tagKeys)
    {
        var values = new List<string>();
        foreach (var key in tagKeys)
        {
            if (info.Tags.TryGetValue(key, out var tagged) && tagged is { Count: > 0 })
            {
                values.AddRange(tagged.Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }

        return values
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AudiomackMatchConfig NormalizeConfig(AudiomackMatchConfig? config)
    {
        var resolved = config ?? new AudiomackMatchConfig();
        if (resolved.SearchLimit < 5)
        {
            resolved.SearchLimit = 5;
        }
        else if (resolved.SearchLimit > 30)
        {
            resolved.SearchLimit = 30;
        }

        return resolved;
    }
}
