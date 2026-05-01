namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class SpotifyMatcher
{
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
        if (!string.IsNullOrWhiteSpace(seededTrackId))
        {
            var byTrackId = await MatchByTrackIdAsync(seededTrackId, info, cancellationToken);
            if (byTrackId != null)
            {
                return byTrackId;
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Isrc))
        {
            var isrcResults = await _client.SearchTracksAsync($"isrc:{info.Isrc}", 20, cancellationToken);
            var byIsrc = isrcResults.FirstOrDefault();
            if (byIsrc != null)
            {
                byIsrc = await _client.EnrichTrackWithPathfinderAsync(byIsrc, cancellationToken);
                EnsureTrackIdentity(byIsrc, seededTrackId, info);
                return new AutoTagMatchResult { Accuracy = 1.0, Track = ToAutoTagTrack(byIsrc) };
            }
        }

        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}";
        var tracks = await _client.SearchTracksAsync(query, 20, cancellationToken);
        if (tracks.Count == 0)
        {
            return null;
        }

        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<SpotifyTrackInfo>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);

        if (match == null)
        {
            return null;
        }

        var enriched = await _client.EnrichTrackWithPathfinderAsync(match.Track, cancellationToken);
        EnsureTrackIdentity(enriched, seededTrackId, info);
        return new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(enriched) };
    }

    private async Task<AutoTagMatchResult?> MatchByTrackIdAsync(string trackId, AutoTagAudioInfo info, CancellationToken cancellationToken)
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

        return new AutoTagMatchResult { Accuracy = 1.0, Track = ToAutoTagTrack(enriched) };
    }

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
        var normalizedUrl = string.Empty;
        if (!string.IsNullOrWhiteSpace(track.Url))
        {
            normalizedUrl = track.Url;
        }
        else if (!string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            normalizedUrl = $"https://open.spotify.com/track/{normalizedTrackId}";
        }

        var mapped = new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            Album = track.Album,
            AlbumArtists = string.IsNullOrWhiteSpace(track.AlbumArtist) ? new List<string>() : new List<string> { track.AlbumArtist },
            Url = normalizedUrl,
            TrackId = normalizedTrackId ?? string.Empty,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            Art = track.Art,
            Isrc = track.Isrc,
            ReleaseDate = track.ReleaseDate,
            Explicit = track.Explicit,
            TrackNumber = track.TrackNumber,
            DiscNumber = track.DiscNumber,
            TrackTotal = track.TrackTotal,
            Label = track.Label,
            Genres = track.Genres.ToList(),
            Danceability = track.Danceability,
            Energy = track.Energy,
            Valence = track.Valence,
            Acousticness = track.Acousticness,
            Instrumentalness = track.Instrumentalness,
            Speechiness = track.Speechiness,
            Loudness = track.Loudness,
            Tempo = track.Tempo,
            TimeSignature = track.TimeSignature,
            Liveness = track.Liveness,
            Key = track.Key,
            Bpm = track.Tempo.HasValue ? (long?)Math.Round(track.Tempo.Value) : null
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
