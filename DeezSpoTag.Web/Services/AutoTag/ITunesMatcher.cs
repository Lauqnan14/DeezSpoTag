namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ItunesMatcher
{
    private readonly ItunesClient _client;
    public ItunesMatcher(ItunesClient client)
    {
        _client = client;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, ItunesMatchConfig itunesConfig, CancellationToken cancellationToken)
    {
        var authoritativeLookupMiss = false;
        if (itunesConfig.MatchById)
        {
            var existingTrackId = AutoTagIdentityTags.ReadAppleTrackId(info);
            if (long.TryParse(existingTrackId, out var numericTrackId) && numericTrackId > 0)
            {
                var lookup = await _client.LookupTrackAsync(existingTrackId, itunesConfig.Country, cancellationToken);
                var lookupTrack = lookup?.ToTrackInfo(itunesConfig);
                if (lookupTrack != null
                    && AutoTagReleaseCategory.MatchesPreference(
                        lookupTrack.ReleaseType,
                        lookupTrack.TrackTotal,
                        config.PreferredReleaseType))
                {
                    return new AutoTagMatchResult
                    {
                        Accuracy = 1.0,
                        Track = ToAutoTagTrack(lookupTrack),
                        MatchStrategy = "id"
                    };
                }

                authoritativeLookupMiss = true;
            }
        }

        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}";
        var results = await _client.SearchAsync(query, itunesConfig.Country, itunesConfig.SearchLimit, cancellationToken);
        if (results?.Results == null || results.Results.Count == 0)
        {
            return null;
        }

        var candidates = results.Results
            .Select(r => r.ToTrackInfo(itunesConfig))
            .Where(r => r != null)
            .Select(r => r!)
            .Where(track => AutoTagReleaseCategory.MatchesPreference(
                track.ReleaseType,
                track.TrackTotal,
                config.PreferredReleaseType))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var existingArtistId = AutoTagIdentityTags.ReadAppleArtistId(info);
        if (!string.IsNullOrWhiteSpace(existingArtistId))
        {
            var artistIdMatches = candidates
                .Where(candidate => string.Equals(candidate.ArtistId, existingArtistId, StringComparison.Ordinal))
                .ToList();
            if (artistIdMatches.Count > 0)
            {
                candidates = artistIdMatches;
            }
        }

        var match = AutoTagMatchSelection.BuildMatchResult(
            info,
            candidates,
            config,
            new OneTaggerMatching.TrackSelectors<ItunesTrackInfo>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            ToAutoTagTrack,
            matchArtist: true);
        if (match != null)
        {
            match.MatchStrategy = authoritativeLookupMiss ? "text_fallback" : "text";
        }

        return match;
    }

    private static AutoTagTrack ToAutoTagTrack(ItunesTrackInfo track)
    {
        var other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(track.ArtistId))
        {
            other["ITUNES_ARTIST_ID"] = new List<string> { track.ArtistId };
        }
        if (!string.IsNullOrWhiteSpace(track.TrackId))
        {
            other[AutoTagIdentityTags.AppleTrackId] = new List<string> { track.TrackId };
            other[AutoTagIdentityTags.ItunesTrackId] = new List<string> { track.TrackId };
        }
        if (!string.IsNullOrWhiteSpace(track.ReleaseId))
        {
            other["APPLE_ALBUM_ID"] = new List<string> { track.ReleaseId };
        }
        if (!string.IsNullOrWhiteSpace(track.Copyright))
        {
            other["copyright"] = new List<string> { track.Copyright };
        }
        if (!string.IsNullOrWhiteSpace(track.TrackId))
        {
            other["source"] = new List<string> { "iTunes" };
            other["sourceId"] = new List<string> { track.TrackId };
        }

        return new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Url = track.Url,
            TrackId = track.TrackId,
            ReleaseId = track.ReleaseId,
            RecordingId = track.TrackId,
            ArtistId = track.ArtistId,
            AlbumId = track.ReleaseId,
            Duration = track.Duration,
            Genres = track.Genres.ToList(),
            ReleaseDate = track.ReleaseDate,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal,
            ReleaseType = AutoTagReleaseCategory.Resolve(track.ReleaseType, track.TrackTotal),
            DiscNumber = track.DiscNumber,
            DiscTotal = track.DiscTotal,
            Isrc = track.Isrc,
            Label = track.Label,
            Explicit = track.Explicit,
            Art = track.Art,
            Other = other
        };
    }
}
