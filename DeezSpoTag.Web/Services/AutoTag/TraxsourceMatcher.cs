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

        var match = AutoTagMatchSelection.SelectBestTrack(
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
        if (match == null)
        {
            return null;
        }

        if (extend)
        {
            try
            {
                await _client.ExtendTrackAsync(match.Value.Track, albumMeta, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed extending Traxsource track.");
            }
        }

        return new AutoTagMatchResult { Accuracy = match.Value.Accuracy, Track = ToAutoTagTrack(match.Value.Track) };
    }

    private static AutoTagTrack ToAutoTagTrack(TraxsourceTrackInfo track)
        => AutoTagTrackFactory.FromTraxsource(track);

}
