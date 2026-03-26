namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ItunesMatcher
{
    private readonly ItunesClient _client;
    private static readonly string[] TrackIdTagKeys =
    {
        "ITUNES_TRACK_ID",
        "ITUNESCATALOGID",
        "ITUNES_TRACKID"
    };
    private static readonly string[] ArtistIdTagKeys =
    {
        "ITUNES_ARTIST_ID",
        "ITUNESARTISTID"
    };

    public ItunesMatcher(ItunesClient client)
    {
        _client = client;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, ItunesMatchConfig itunesConfig, CancellationToken cancellationToken)
    {
        if (itunesConfig.MatchById)
        {
            var existingTrackId = AutoTagTagValueReader.ReadFirstTagValue(info, TrackIdTagKeys);
            var lookup = await _client.LookupTrackAsync(existingTrackId, itunesConfig.Country, cancellationToken);
            var lookupTrack = lookup?.ToTrackInfo(itunesConfig);
            if (lookupTrack != null)
            {
                return new AutoTagMatchResult { Accuracy = 1.0, Track = ToAutoTagTrack(lookupTrack) };
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
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var existingArtistId = AutoTagTagValueReader.ReadFirstTagValue(info, ArtistIdTagKeys);
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

        return AutoTagMatchSelection.BuildMatchResult(
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
    }

    private static AutoTagTrack ToAutoTagTrack(ItunesTrackInfo track)
    {
        var other = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(track.ArtistId))
        {
            other["ITUNES_ARTIST_ID"] = new List<string> { track.ArtistId };
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
            Duration = track.Duration,
            Genres = track.Genres.ToList(),
            ReleaseDate = track.ReleaseDate,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal,
            DiscNumber = track.DiscNumber,
            Isrc = track.Isrc,
            Label = track.Label,
            Explicit = track.Explicit,
            Art = track.Art,
            Other = other
        };
    }
}
