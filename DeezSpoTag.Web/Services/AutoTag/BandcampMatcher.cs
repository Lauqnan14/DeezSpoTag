namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BandcampMatcher
{
    private readonly BandcampClient _client;
    private readonly ILogger<BandcampMatcher> _logger;

    public BandcampMatcher(BandcampClient client, ILogger<BandcampMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, CancellationToken cancellationToken)
    {
        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}";
        var results = await _client.SearchTracksAsync(query, cancellationToken);
        if (results.Count == 0)
        {
            return null;
        }

        var candidates = results.Select(r => r.ToTrackInfo()).ToList();
        var match = AutoTagMatchSelection.SelectBestTrack(
            info,
            candidates,
            config,
            new OneTaggerMatching.TrackSelectors<BandcampTrackInfo>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                _ => null,
                track => track.ReleaseDate),
            matchArtist: true);
        if (match == null)
        {
            return null;
        }

        try
        {
            var full = await _client.GetTrackAsync(match.Value.Track.Url, cancellationToken);
            if (full != null)
            {
                var detailed = full.ToTrackInfo();
                return new AutoTagMatchResult { Accuracy = match.Value.Accuracy, Track = ToAutoTagTrack(detailed) };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to extend Bandcamp track.");
        }

        return null;
    }

    private static AutoTagTrack ToAutoTagTrack(BandcampTrackInfo track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Label = track.Label,
            Genres = track.Genres.ToList(),
            Styles = track.Styles.ToList(),
            Art = track.Art,
            Url = track.Url,
            TrackId = track.TrackId,
            ReleaseId = track.ReleaseId,
            TrackTotal = track.TrackTotal,
            ReleaseDate = track.ReleaseDate
        };
    }
}
