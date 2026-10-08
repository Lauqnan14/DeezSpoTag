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
/// the descriptor deliberately does not claim BPM, key or lyrics (Audiomack
/// exposes none), but it does claim ISRC — the payload carries <c>isrc</c> and the
/// matcher maps it.
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
        if (!TryGetCoherentTaggedIdentity(info, out var taggedTrackIds, out var taggedSongPaths, out var contradiction))
        {
            _logger.LogDebug("Audiomack matching aborted: {Reason}", contradiction);
            return null;
        }

        // A tagged identity that resolves to a different song than another
        // Audiomack-specific tag claims is a contradiction, not a lookup miss.
        // It is decided before any lookup: neither an ID match nor text search
        // may silently pick a winner for contradictory tags.
        if (taggedTrackIds.Count > 0 && taggedSongPaths.Count > 0
            && !await TagsAgree(taggedTrackIds, taggedSongPaths, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Audiomack matching aborted: tagged id and tagged url disagree on the song");
            return null;
        }

        if (resolvedConfig.MatchById)
        {
            var byId = await TryMatchByIdAsync(info, resolvedConfig, cancellationToken, matchingConfig, taggedTrackIds, taggedSongPaths).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        AutoTagMatchingConfig matchingConfig,
        IReadOnlyList<string> taggedTrackIds,
        IReadOnlyList<string> taggedSongPaths)
    {
        var trackIds = taggedTrackIds;
        var songPaths = taggedSongPaths;

        // Coherent tagged track id(s) first, then tagged song paths.
        foreach (var trackId in trackIds)
        {
            var candidates = await _apiClient.SearchSongsAsync(trackId, config.SearchLimit, cancellationToken).ConfigureAwait(false);
            var exact = candidates.FirstOrDefault(candidate =>
                string.Equals(AudiomackIdNormalizer.NormalizeTrackId(candidate.Id), trackId, StringComparison.Ordinal)
                && !AudiomackTaxonomy.IsNonMusicCandidate(candidate));
            if (exact == null)
            {
                continue;
            }

            var hydrated = await HydrateSelectedAsync(exact, cancellationToken).ConfigureAwait(false);
            if (hydrated == null || AudiomackTaxonomy.IsNonMusicCandidate(hydrated))
            {
                continue;
            }

            if (!TagsCorroborate(hydrated, trackIds, songPaths))
            {
                _logger.LogDebug("Audiomack id-match rejected: hydrated identity contradicts the tagged identity");
                continue;
            }

            if (!AudiomackIdNormalizer.TryGetSongIdentity(hydrated, out _, out _, out _))
            {
                _logger.LogDebug("Audiomack id-match rejected: incoherent-identity");
                continue;
            }

            if (!EvaluateCandidate(info, hydrated, matchingConfig, out var reason))
            {
                _logger.LogDebug("Audiomack id-match rejected: {Reason}", reason);
                continue;
            }

            var result = ToMatchResult(hydrated, accuracy: 1.0d, "id", info);
            if (result != null)
            {
                return result;
            }
        }

        // Path lookup: an embedded Audiomack URL carries the artist/song slugs.
        foreach (var parts in songPaths.Select(songPath => songPath.Split('/', 2)))
        {
            var song = await _apiClient.GetSongAsync(parts[0], parts[1], cancellationToken).ConfigureAwait(false);
            if (song == null)
            {
                continue;
            }

            var hydrated = await HydrateSelectedAsync(song, cancellationToken, alreadyFetchedSong: true).ConfigureAwait(false);
            if (hydrated == null || AudiomackTaxonomy.IsNonMusicCandidate(hydrated))
            {
                continue;
            }

            if (!TagsCorroborate(hydrated, trackIds, songPaths))
            {
                _logger.LogDebug("Audiomack url-match rejected: hydrated identity contradicts the tagged identity");
                continue;
            }

            if (!AudiomackIdNormalizer.TryGetSongIdentity(hydrated, out _, out _, out _))
            {
                _logger.LogDebug("Audiomack url-match rejected: incoherent-identity");
                continue;
            }

            if (!EvaluateCandidate(info, hydrated, matchingConfig, out var reason))
            {
                _logger.LogDebug("Audiomack url-match rejected: {Reason}", reason);
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

    private static bool TagsCorroborate(
        AudiomackSongCandidate hydrated,
        IReadOnlyList<string> trackIds,
        IReadOnlyList<string> songPaths)
    {
        if (trackIds.Any(expectedId => !string.Equals(AudiomackIdNormalizer.NormalizeTrackId(hydrated.Id), expectedId, StringComparison.Ordinal)))
        {
            return false;
        }

        if (songPaths.Count > 0)
        {
            string? hydratedPath = null;
            if (AudiomackIdNormalizer.TryGetSongIdentity(hydrated, out _, out var path, out _) && path is not null)
            {
                hydratedPath = path;
            }
            else if (!string.IsNullOrWhiteSpace(hydrated.Url) || !string.IsNullOrWhiteSpace(hydrated.UrlSlug))
            {
                // Payload claims a URL but it cannot be validated: cannot corroborate.
                return false;
            }

            if (songPaths.Any(expectedPath => hydratedPath is not null
                && !string.Equals(hydratedPath, expectedPath, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the tagged track id through the signed API and requires the
    /// result to describe the same song as the tagged URL. Audiomack song URLs
    /// carry no numeric id, so agreement can only be decided this way.
    /// </summary>
    private async Task<bool> TagsAgree(
        IReadOnlyList<string> trackIds,
        IReadOnlyList<string> songPaths,
        CancellationToken cancellationToken)
    {
        if (trackIds.Count == 0 || songPaths.Count == 0)
        {
            return true;
        }

        var taggedId = trackIds[0];
        var taggedPath = songPaths[0];
        var expected = new AudiomackSongCandidate(
            Id: taggedId, Title: null, Artist: null, Album: null, Genre: null, Mood: null,
            Isrc: null, Label: null, DurationSeconds: null, ArtworkUrl: null,
            ReleasedDate: null, Url: null, UrlSlug: null, ArtistSlug: null,
            UploaderName: null, AlbumId: null);

        var parts = taggedPath.Split('/', 2);
        var song = await _apiClient.GetSongAsync(parts[0], parts[1], cancellationToken).ConfigureAwait(false);
        if (song == null)
        {
            // The tagged URL no longer resolves; that is an unavailable
            // identifier, not proof of a contradiction.
            return true;
        }

        return AudiomackIdNormalizer.IsSameSong(expected, song, out _);
    }

    /// <summary>
    /// The embedded Audiomack-specific ID/URL tags must agree with each other
    /// before any lookup is attempted; otherwise no ID match is produced and
    /// the disagreement is not resolved silently through text search.
    /// </summary>
    private static bool TryGetCoherentTaggedIdentity(
        AutoTagAudioInfo info,
        out IReadOnlyList<string> trackIds,
        out IReadOnlyList<string> songPaths,
        out string contradiction)
    {
        contradiction = string.Empty;
        trackIds = ReadTagValues(info, TrackIdTagKeys)
            .Select(AudiomackIdNormalizer.NormalizeTrackId)
            .Where(value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

        var specificUrls = ReadTagValues(info, new[] { "AUDIOMACK_URL" });
        var genericUrls = ReadTagValues(info, UrlTagKeys.Where(key => !string.Equals(key, "AUDIOMACK_URL", StringComparison.OrdinalIgnoreCase)).ToArray());
        var usableUrls = specificUrls.Count > 0 ? specificUrls : genericUrls;
        var paths = new List<string>();
        foreach (var url in usableUrls)
        {
            if (AudiomackIdNormalizer.TryGetCanonicalSongPath(url, out var path))
            {
                paths.Add(path);
            }
        }

        songPaths = paths.Distinct(StringComparer.Ordinal).ToList();
        if (trackIds.Count > 1)
        {
            contradiction = "conflicting-tagged-track-ids";
            return false;
        }

        if (songPaths.Count > 1)
        {
            contradiction = "conflicting-tagged-song-urls";
            return false;
        }

        return true;
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

        // Cheap gates first: the uploader never substitutes for a recording artist,
        // and each candidate must survive the source-recording evidence gate
        // before it earns a hydration slot.
        var usable = candidates
            .Where(candidate => !AudiomackTaxonomy.IsNonMusicCandidate(candidate))
            .Where(HasRecordingArtist)
            .Select(candidate => (Candidate: candidate, Track: ToAutoTagTrack(candidate)))
            .Where(pair => pair.Track != null)
            .Select(pair => (pair.Candidate, Track: pair.Track!))
            .Where(pair => EvaluateCandidate(info, pair.Candidate, matchingConfig, out _))
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        // Collapse repeated entries for the same Audiomack ID/path before ranking.
        var unique = new List<(AudiomackSongCandidate Candidate, AutoTagTrack Track)>();
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in usable)
        {
            var key = AudiomackIdNormalizer.NormalizeTrackId(pair.Candidate.Id)
                ?? (AudiomackIdNormalizer.TryGetCanonicalSongPath(pair.Candidate.Url, out var path) ? path : pair.Candidate.UrlSlug);
            if (key is null || seenIdentities.Add(key))
            {
                unique.Add(pair);
            }
        }

        var selectors = new OneTaggerMatching.TrackSelectors<AutoTagTrack>(
            track => track.Title,
            _ => null,
            track => track.Artists.Count > 0 ? track.Artists : track.AlbumArtists,
            track => track.Duration,
            track => track.ReleaseDate);
        var ranked = OneTaggerMatching.MatchTrackRanked(
            info,
            unique.Select(pair => pair.Track).ToList(),
            matchingConfig,
            selectors,
            matchArtist: true);
        if (ranked.Count == 0)
        {
            return null;
        }

        var byTrack = unique.ToDictionary(pair => pair.Track, ReferenceEqualityComparer.Instance);
        // MatchTrackRanked can list the same track twice (exact fallback plus its
        // fuzzy entry). Hydrate each Audiomack identity exactly once.
        var ordered = new List<(OneTaggerMatching.MatchSelection<AutoTagTrack> Selection, (AudiomackSongCandidate Candidate, AutoTagTrack Track) Pair)>();
        var queued = new HashSet<AutoTagTrack>(ReferenceEqualityComparer.Instance);
        foreach (var selection in ranked)
        {
            if (!queued.Add(selection.Track))
            {
                continue;
            }

            ordered.Add((selection, byTrack[selection.Track]));
        }

        ordered = ordered
            .OrderByDescending(entry => HasMatchingIsrc(info, entry.Pair.Candidate))
            .ThenByDescending(entry => entry.Selection.Accuracy)
            .ThenByDescending(entry => MatchesKnownAlbum(info, entry.Pair.Candidate))
            .ToList();

        var completed = new List<(AudiomackSongCandidate Candidate, AutoTagTrack Track)>();

        foreach (var entry in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hydrated = await HydrateSelectedAsync(entry.Pair.Candidate, cancellationToken).ConfigureAwait(false);
            if (hydrated == null || AudiomackTaxonomy.IsNonMusicCandidate(hydrated))
            {
                continue;
            }

            // The completed metadata must still prove the selected recording and
            // pass every source gate; the pre-hydration score is never reused.
            if (!AudiomackIdNormalizer.TryGetSongIdentity(hydrated, out _, out _, out _))
            {
                _logger.LogDebug("Audiomack hydrated candidate rejected: incoherent-identity");
                continue;
            }

            if (!AudiomackIdNormalizer.IsSameSong(entry.Pair.Candidate, hydrated, out _))
            {
                _logger.LogDebug("Audiomack hydrated candidate rejected: detail response conflicts with the selected identity");
                continue;
            }

            if (!EvaluateCandidate(info, hydrated, matchingConfig, out _))
            {
                _logger.LogDebug("Audiomack hydrated candidate rejected");
                continue;
            }

            var finalTrack = ToAutoTagTrack(hydrated);
            if (finalTrack == null)
            {
                continue;
            }

            completed.Add((hydrated, finalTrack));
        }

        // Rank and resolve ambiguity using completed evidence, including identifiers
        // and album information that were absent from the search response.
        var completedByTrack = completed.ToDictionary(pair => pair.Track);
        var finalRanked = OneTaggerMatching.MatchTrackRanked(
                info, completed.Select(pair => pair.Track).ToList(), matchingConfig, selectors, matchArtist: true)
            .DistinctBy(selection => selection.Track)
            .OrderByDescending(selection => HasMatchingIsrc(info, completedByTrack[selection.Track].Candidate))
            .ThenByDescending(selection => selection.Accuracy)
            .ThenByDescending(selection => MatchesKnownAlbum(info, completedByTrack[selection.Track].Candidate))
            .ToList();
        if (finalRanked.Count == 0)
        {
            return null;
        }

        if (finalRanked.Count > 1 && matchingConfig.MultipleMatches == MultipleMatchesSort.Default
            && IsEquallySupported(finalRanked[0], finalRanked[1], info,
                completedByTrack[finalRanked[0].Track].Candidate, completedByTrack[finalRanked[1].Track].Candidate)
            && !SameIdentity(completedByTrack[finalRanked[0].Track].Candidate, completedByTrack[finalRanked[1].Track].Candidate))
        {
            _logger.LogDebug("Audiomack completed text-match ambiguous: equally supported different recordings");
            return null;
        }

        return new AutoTagMatchResult
        {
            Accuracy = finalRanked[0].Accuracy,
            Track = finalRanked[0].Track,
            MatchStrategy = "text"
        };
    }

    private static bool HasRecordingArtist(AudiomackSongCandidate candidate)
        => !string.IsNullOrWhiteSpace(candidate.Artist)
           || candidate.Artists.Any(value => !string.IsNullOrWhiteSpace(value));

    private static bool HasMatchingIsrc(AutoTagAudioInfo info, AudiomackSongCandidate candidate)
    {
        var source = AudiomackIdNormalizer.NormalizeIsrc(info.Isrc);
        var target = AudiomackIdNormalizer.NormalizeIsrc(candidate.Isrc);
        return source is not null && target is not null && string.Equals(source, target, StringComparison.Ordinal);
    }

    private static bool MatchesKnownAlbum(AutoTagAudioInfo info, AudiomackSongCandidate candidate)
        => !string.IsNullOrWhiteSpace(info.Album)
           && !string.IsNullOrWhiteSpace(candidate.Album)
           && string.Equals(info.Album.Trim(), candidate.Album.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsEquallySupported(
        OneTaggerMatching.MatchSelection<AutoTagTrack> first,
        OneTaggerMatching.MatchSelection<AutoTagTrack> second,
        AutoTagAudioInfo info,
        AudiomackSongCandidate firstCandidate,
        AudiomackSongCandidate secondCandidate)
        => OneTaggerMatching.ScoresEqual(first.Accuracy, second.Accuracy)
           && HasMatchingIsrc(info, firstCandidate) == HasMatchingIsrc(info, secondCandidate)
           && MatchesKnownAlbum(info, firstCandidate) == MatchesKnownAlbum(info, secondCandidate);

    private static bool SameIdentity(AudiomackSongCandidate first, AudiomackSongCandidate second)
        => AudiomackIdNormalizer.IsSameSong(first, second, out _);

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
            Barcode = string.IsNullOrWhiteSpace(song.Upc) ? null : song.Upc.Trim(),
            Explicit = ParseExplicit(song.Explicit),
            Art = song.ArtworkUrl,
            Url = song.Url,
            TrackId = song.Id,
            // Audiomack's own uploader id is the artist id. There is no distinct
            // album-artist id in its payload, so none is aliased into that field.
            ArtistId = string.IsNullOrWhiteSpace(song.UploaderId) ? null : song.UploaderId.Trim(),
            // Audiomack exposes a single album-scoped id (album_id); it is carried in
            // AlbumId only. It is not aliased into ReleaseId, because Audiomack has no
            // separate release entity to identify.
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

    private async Task<AudiomackSongCandidate?> HydrateSelectedAsync(
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
                    if (AudiomackTaxonomy.TryMerge(current, detailed, out var merged, out var reason))
                    {
                        current = merged;
                    }
                    else
                    {
                        // The detail response describes a different song than the
                        // selected candidate: this candidate must not survive
                        // hydration, and the pre-hydration score is never reused.
                        _logger.LogDebug("Audiomack detail overlay rejected: {Reason}", reason);
                        return null;
                    }
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
            var page = await _apiClient.GetPublicPageSongAsync(current, cancellationToken).ConfigureAwait(false);
            if (page != null)
            {
                if (AudiomackTaxonomy.TryMerge(current, page, out var merged, out var reason))
                {
                    current = merged;
                }
                else
                {
                    _logger.LogDebug("Audiomack page overlay rejected: {Reason}", reason);
                }
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
        // Upload ownership never stands in for the recording artist; the
        // uploader id is still available as Audiomack's artist id.
        return artists;
    }

    /// <summary>
    /// Audiomack-local source-recording gates applied on every selection route,
    /// before hydration (cheap) and after hydration (final). An optional field
    /// that is missing is not a failure; a populated field that conflicts with
    /// the source recording always is.
    /// </summary>
    public static bool EvaluateCandidate(
        AutoTagAudioInfo info,
        AudiomackSongCandidate candidate,
        AutoTagMatchingConfig config,
        out string? rejectionReason)
    {
        var sourceIsrc = AudiomackIdNormalizer.NormalizeIsrc(info.Isrc);
        var candidateIsrc = AudiomackIdNormalizer.NormalizeIsrc(candidate.Isrc);
        if (sourceIsrc is not null && candidateIsrc is not null
            && !string.Equals(sourceIsrc, candidateIsrc, StringComparison.Ordinal))
        {
            rejectionReason = "isrc-mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            rejectionReason = "missing-recording-title";
            return false;
        }

        if (!HasRecordingArtist(candidate))
        {
            rejectionReason = "missing-recording-artist";
            return false;
        }

        if (DeezSpoTag.Core.Utils.TrackTitleMatcher.HasVersionDrift(info.Title, candidate.Title))
        {
            rejectionReason = "version-drift";
            return false;
        }

        if (config.MatchDuration
            && info.DurationSeconds is > 0
            && candidate.DurationSeconds is > 0
            && Math.Abs(info.DurationSeconds.Value - candidate.DurationSeconds.Value) > config.MaxDurationDifferenceSeconds)
        {
            rejectionReason = "duration-mismatch";
            return false;
        }

        var sourceArtists = info.Artists.Count > 0 ? info.Artists
            : string.IsNullOrWhiteSpace(info.Artist) ? new List<string>() : new List<string> { info.Artist };
        if (sourceArtists.Count > 0 && !OneTaggerMatching.MatchArtist(sourceArtists, CollectArtists(candidate), config.Strictness))
        {
            rejectionReason = "recording-artist-mismatch";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(info.Title))
        {
            var track = ToAutoTagTrack(candidate)!;
            var selectors = new OneTaggerMatching.TrackSelectors<AutoTagTrack>(
                value => value.Title, _ => null, value => value.Artists,
                value => value.Duration, value => value.ReleaseDate);
            if (OneTaggerMatching.MatchTrack(info, new[] { track }, config, selectors, matchArtist: true) == null)
            {
                rejectionReason = "recording-title-mismatch";
                return false;
            }
        }

        var sourceGuests = ParseSourceGuests(info);
        var candidateGuests = ParseCandidateGuests(candidate);
        if (sourceGuests.Count > 0 && candidateGuests.Count > 0 && !sourceGuests.Overlaps(candidateGuests))
        {
            rejectionReason = "conflicting-guest-credits";
            return false;
        }

        if (candidateGuests.Except(sourceGuests, StringComparer.Ordinal).Any()
            && (sourceIsrc is null || candidateIsrc is null
                || !string.Equals(sourceIsrc, candidateIsrc, StringComparison.Ordinal)))
        {
            // A candidate introducing guests the source does not name must prove
            // the identity through ISRC; a cleaned title alone never proves it.
            rejectionReason = "introduced-guest-requires-isrc";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    private static HashSet<string> ParseSourceGuests(AutoTagAudioInfo info)
    {
        var guests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var guest in AudiomackIdNormalizer.ParseTitleFeaturedGuests(info.Title))
        {
            guests.Add(guest);
        }

        foreach (var artistField in new[] { info.Artist }.Concat(info.Artists))
        {
            foreach (var guest in AudiomackIdNormalizer.ParseTitleFeaturedGuests(artistField))
            {
                guests.Add(guest);
            }
        }

        return guests;
    }

    private static HashSet<string> ParseCandidateGuests(AudiomackSongCandidate candidate)
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

    /// <summary>
    /// Audiomack sends explicitness as "yes"/"no" (sometimes 1/0). Unknown shapes
    /// yield null so no explicit tag is written on a guess.
    /// </summary>
    private static bool? ParseExplicit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "explicit" => true,
            "no" or "false" or "0" or "clean" or "none" => false,
            _ => null
        };
    }

    private static DateTime? ParseReleasedDate(string raw)    {
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
