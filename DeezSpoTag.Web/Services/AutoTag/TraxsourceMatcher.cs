namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class TraxsourceMatcher
{
    private readonly TraxsourceClient _client;
    private readonly ILogger<TraxsourceMatcher> _logger;

    public TraxsourceMatcher(TraxsourceClient client, ILogger<TraxsourceMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, bool extend, bool albumMeta, CancellationToken cancellationToken)
    {
        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}";
        var tracks = await _client.SearchTracksAsync(query, cancellationToken);
        if (tracks.Count == 0)
        {
            return null;
        }

        var match = MatchTracks(info, tracks, config);
        if (match == null)
        {
            return null;
        }

        if (extend)
        {
            try
            {
                await _client.ExtendTrackAsync(match.Track, albumMeta, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed extending Traxsource track.");
            }
        }

        return new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(match.Track) };
    }

    private static MatchCandidate? MatchTracks(AutoTagAudioInfo info, List<TraxsourceTrackInfo> tracks, AutoTagMatchingConfig config)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<TraxsourceTrackInfo>(
                track => track.Title,
                track => track.Version,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);

        return match == null ? null : new MatchCandidate(match.Accuracy, match.Track);
    }

    private static AutoTagTrack ToAutoTagTrack(TraxsourceTrackInfo track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Version = track.Version,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Key = track.Key,
            Bpm = track.Bpm,
            Genres = track.Genres.ToList(),
            Art = track.Art,
            Url = track.Url,
            Label = track.Label,
            CatalogNumber = track.CatalogNumber,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal,
            ReleaseDate = track.ReleaseDate
        };
    }

    private sealed record MatchCandidate(double Accuracy, TraxsourceTrackInfo Track);
}
