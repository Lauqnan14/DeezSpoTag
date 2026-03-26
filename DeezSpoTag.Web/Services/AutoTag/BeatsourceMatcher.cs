namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatsourceMatcher
{
    private readonly BeatsourceClient _client;

    public BeatsourceMatcher(BeatsourceClient client, ILogger<BeatsourceMatcher> logger)
    {
        _client = client;
        _ = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, BeatsourceMatchConfig beatsourceConfig, CancellationToken cancellationToken)
    {
        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}";
        var response = await _client.SearchAsync(query, cancellationToken);
        if (response?.Tracks == null || response.Tracks.Count == 0)
        {
            return null;
        }

        var candidates = response.Tracks.Select(t => t.ToTrackInfo(beatsourceConfig)).ToList();
        return AutoTagMatchSelection.BuildMatchResult(
            info,
            candidates,
            config,
            new OneTaggerMatching.TrackSelectors<BeatsourceTrackInfo>(
                track => track.Title,
                track => track.Version,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            ToAutoTagTrack,
            matchArtist: true);
    }

    private static AutoTagTrack ToAutoTagTrack(BeatsourceTrackInfo track)
        => AutoTagTrackFactory.FromBeatsource(track);
}
