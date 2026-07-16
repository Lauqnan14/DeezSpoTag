namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class SpotifyMatcher
{
    private const int IsrcAuthority = 2;
    private const int SearchAuthority = 1;

    private static readonly string[] TrackIdTagKeys =
    {
        "SPOTIFY_TRACK_ID",
        "SPOTIFY_TRACKID",
        "SPOTIFYID",
        "SPOTIFY_ID"
    };

    private static readonly string[] TrackUrlTagKeys =
    {
        "SPOTIFY_URL",
        "SHAZAM_SPOTIFY_URL",
        "SPOTIFY_URI",
        "SPOTIFYURI",
        "URL",
        "WWWAUDIOFILE"
    };

    private readonly SpotifyClient _client;

    public SpotifyMatcher(SpotifyClient client)
    {
        _client = client;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, CancellationToken cancellationToken)
    {
        var seededTrackId = TryResolveTrackId(info);
        var candidates = new List<SpotifyCandidate>();
        if (!string.IsNullOrWhiteSpace(seededTrackId))
        {
            var byTrackId = await GetTrackIdCandidateAsync(seededTrackId, info, cancellationToken);
            if (byTrackId != null && MatchesKnownReleasePreference(byTrackId, config.PreferredReleaseType))
            {
                return new AutoTagMatchResult
                {
                    Accuracy = 1.0,
                    Track = ToAutoTagTrack(byTrackId),
                    MatchStrategy = "id"
                };
            }

            // A valid embedded Spotify ID is authoritative. If it cannot be
            // resolved, do not replace it with a potentially different search result.
            return null;
        }

        if (!string.IsNullOrWhiteSpace(info.Isrc))
        {
            var isrcResults = await _client.SearchTracksAsync($"isrc:{info.Isrc}", 20, cancellationToken);
            candidates.AddRange(isrcResults.Select(track => new SpotifyCandidate(track, IsrcAuthority)));
        }

        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}";
        var tracks = await _client.SearchTracksAsync(query, 20, cancellationToken);
        candidates.AddRange(tracks.Select(track => new SpotifyCandidate(track, SearchAuthority)));
        var match = SelectBestCandidate(info, candidates, config);

        if (match == null)
        {
            return null;
        }

        var enriched = await _client.EnrichTrackWithPathfinderAsync(match.Track.Track, cancellationToken);
        EnsureTrackIdentity(enriched, seededTrackId, info);
        return new AutoTagMatchResult
        {
            Accuracy = match.Accuracy,
            Track = ToAutoTagTrack(enriched),
            MatchStrategy = match.Track.Authority switch
            {
                IsrcAuthority => "isrc",
                _ => "text"
            }
        };
    }

    private static bool MatchesKnownReleasePreference(SpotifyTrackInfo track, string? preferredReleaseType)
    {
        var resolvedReleaseType = AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal);
        return string.IsNullOrWhiteSpace(resolvedReleaseType)
            || AutoTagReleaseCategory.MatchesPreference(
                resolvedReleaseType,
                track.TrackTotal,
                preferredReleaseType);
    }

    private static OneTaggerMatching.MatchSelection<SpotifyCandidate>? SelectBestCandidate(
        AutoTagAudioInfo info,
        IReadOnlyList<SpotifyCandidate> candidates,
        AutoTagMatchingConfig config)
    {
        var deduped = DeduplicateCandidates(candidates)
            .OrderByDescending(candidate => candidate.Authority)
            .ToList();
        return MatchCandidate(info, deduped, config);
    }

    private static OneTaggerMatching.MatchSelection<SpotifyCandidate>? MatchCandidate(
        AutoTagAudioInfo info,
        IReadOnlyList<SpotifyCandidate> candidates,
        AutoTagMatchingConfig config)
    {
        var compatibleTracks = candidates
            .Where(candidate => HasCompatibleArtistIdentity(info, candidate.Track.Artists, config))
            .Where(candidate => AutoTagReleaseCategory.MatchesPreference(
                candidate.Track.ReleaseType,
                candidate.Track.TrackTotal,
                config.PreferredReleaseType))
            .ToList();
        if (compatibleTracks.Count == 0)
        {
            return null;
        }

        return OneTaggerMatching.MatchTrack(
            info,
            compatibleTracks,
            config,
            new OneTaggerMatching.TrackSelectors<SpotifyCandidate>(
                candidate => candidate.Track.Title,
                _ => null,
                candidate => candidate.Track.Artists,
                candidate => candidate.Track.Duration,
                candidate => candidate.Track.ReleaseDate),
            matchArtist: true);
    }

    private static List<SpotifyCandidate> DeduplicateCandidates(IReadOnlyList<SpotifyCandidate> candidates)
    {
        var deduped = new Dictionary<string, SpotifyCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var key = NormalizeTrackId(candidate.Track.TrackId)
                ?? NormalizeTrackId(candidate.Track.Url)
                ?? $"{candidate.Track.Title}|{string.Join(',', candidate.Track.Artists)}|{candidate.Track.Duration.TotalSeconds:0}";
            if (deduped.TryGetValue(key, out var existing) && existing.Authority >= candidate.Authority)
            {
                continue;
            }

            deduped[key] = candidate;
        }

        return deduped.Values.ToList();
    }

    private static bool HasCompatibleArtistIdentity(
        AutoTagAudioInfo info,
        IReadOnlyList<string> candidateArtists,
        AutoTagMatchingConfig config)
    {
        IReadOnlyList<string> sourceArtists;
        if (info.Artists.Count > 0)
        {
            sourceArtists = info.Artists;
        }
        else if (string.IsNullOrWhiteSpace(info.Artist))
        {
            sourceArtists = [];
        }
        else
        {
            sourceArtists = [info.Artist];
        }

        var normalizedSource = sourceArtists
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();
        var normalizedCandidate = candidateArtists
            .Select(NormalizeArtistIdentity)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .ToList();

        if (normalizedSource.Count == 0 || normalizedCandidate.Count == 0)
        {
            return true;
        }

        if (normalizedSource.Any(source => normalizedCandidate.Contains(source, StringComparer.Ordinal)))
        {
            return true;
        }

        var similarity = AutoTagSimilarity.ComputeScore(
            string.Join(" ", normalizedSource),
            string.Join(" ", normalizedCandidate));
        var strictness = Math.Clamp(config.Strictness - 0.05d, 0.45d, 0.95d);
        return similarity >= Math.Clamp(strictness + 0.15d, 0.80d, 0.98d);
    }

    private static string NormalizeArtistIdentity(string value)
    {
        return AutoTagSimilarity.NormalizeText(value);
    }

    private async Task<SpotifyTrackInfo?> GetTrackIdCandidateAsync(string trackId, AutoTagAudioInfo info, CancellationToken cancellationToken)
    {
        var seeded = new SpotifyTrackInfo
        {
            TrackId = trackId,
            Url = $"https://open.spotify.com/track/{trackId}",
            Isrc = info.Isrc
        };

        var enriched = await _client.EnrichTrackWithPathfinderAsync(seeded, cancellationToken);
        EnsureTrackIdentity(enriched, trackId, info);
        if (string.IsNullOrWhiteSpace(enriched.TrackId))
        {
            return null;
        }

        return enriched;
    }

    private sealed record SpotifyCandidate(SpotifyTrackInfo Track, int Authority);

    private static string? TryResolveTrackId(AutoTagAudioInfo info)
    {
        var directValue = AutoTagTagValueReader.ReadFirstTagValue(info, TrackIdTagKeys);
        var fromDirectValue = NormalizeTrackId(directValue);
        if (!string.IsNullOrWhiteSpace(fromDirectValue))
        {
            return fromDirectValue;
        }

        var urlValue = AutoTagTagValueReader.ReadFirstTagValue(info, TrackUrlTagKeys);
        return NormalizeTrackId(urlValue);
    }

    private static string? NormalizeTrackId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (SpotifyMetadataService.TryParseSpotifyUrl(trimmed, out var type, out var parsedId)
            && type.Equals("track", StringComparison.OrdinalIgnoreCase)
            && IsLikelySpotifyTrackId(parsedId))
        {
            return parsedId;
        }

        return IsLikelySpotifyTrackId(trimmed) ? trimmed : null;
    }

    private static bool IsLikelySpotifyTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }

        return value.All(char.IsLetterOrDigit);
    }

    private static AutoTagTrack ToAutoTagTrack(SpotifyTrackInfo track)
    {
        var normalizedTrackId = NormalizeTrackId(track.TrackId) ?? NormalizeTrackId(track.Url);
        var normalizedUrl = !string.IsNullOrWhiteSpace(normalizedTrackId)
            ? $"https://open.spotify.com/track/{normalizedTrackId}"
            : string.Empty;

        var mapped = new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            Album = track.Album,
            AlbumArtists = string.IsNullOrWhiteSpace(track.AlbumArtist) ? new List<string>() : new List<string> { track.AlbumArtist },
            Url = normalizedUrl,
            TrackId = normalizedTrackId ?? string.Empty,
            ReleaseId = track.ReleaseId,
            RecordingId = normalizedTrackId,
            AlbumId = track.ReleaseId,
            Duration = track.Duration,
            Art = track.Art,
            Isrc = track.Isrc,
            ReleaseDate = track.ReleaseDate,
            Explicit = track.Explicit,
            TrackNumber = track.TrackNumber,
            DiscNumber = track.DiscNumber,
            TrackTotal = track.TrackTotal,
            ReleaseType = AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal),
            Label = track.Label,
            Genres = track.Genres.ToList()
        };

        if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            mapped.Other["SPOTIFY_TRACK_ID"] = new List<string> { normalizedTrackId };
            mapped.Other["SOURCE"] = new List<string> { "SPOTIFY" };
            mapped.Other["SOURCEID"] = new List<string> { normalizedTrackId };
        }

        if (!string.IsNullOrWhiteSpace(normalizedUrl))
        {
            mapped.Other["SPOTIFY_URL"] = new List<string> { normalizedUrl };
        }

        return mapped;
    }

    private static void EnsureTrackIdentity(SpotifyTrackInfo track, string? preferredTrackId, AutoTagAudioInfo source)
    {
        var normalizedTrackId = NormalizeTrackId(track.TrackId)
            ?? NormalizeTrackId(track.Url)
            ?? NormalizeTrackId(preferredTrackId)
            ?? TryResolveTrackId(source);
        if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            track.TrackId = normalizedTrackId;
            if (string.IsNullOrWhiteSpace(track.Url))
            {
                track.Url = $"https://open.spotify.com/track/{normalizedTrackId}";
            }
        }

        if (string.IsNullOrWhiteSpace(track.Isrc) && !string.IsNullOrWhiteSpace(source.Isrc))
        {
            track.Isrc = source.Isrc;
        }

        if (string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(source.Title))
        {
            track.Title = source.Title;
        }

        if (track.Artists.Count == 0)
        {
            if (source.Artists.Count > 0)
            {
                track.Artists = source.Artists
                    .Where(artist => !string.IsNullOrWhiteSpace(artist))
                    .Select(artist => artist.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else if (!string.IsNullOrWhiteSpace(source.Artist))
            {
                track.Artists = new List<string> { source.Artist.Trim() };
            }
        }

        if (string.IsNullOrWhiteSpace(track.Album) && !string.IsNullOrWhiteSpace(source.Album))
        {
            track.Album = source.Album;
        }
    }
}
