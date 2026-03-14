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
        var match = MatchTracks(info, candidates, config);
        if (match == null)
        {
            return null;
        }

        return new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(match.Track) };
    }

    private static MatchCandidate? MatchTracks(AutoTagAudioInfo info, List<BeatsourceTrackInfo> tracks, AutoTagMatchingConfig config)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<BeatsourceTrackInfo>(
                track => track.Title,
                track => track.Version,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);

        return match == null ? null : new MatchCandidate(match.Accuracy, match.Track);
    }

    private sealed record MatchCandidate(double Accuracy, BeatsourceTrackInfo Track);

    private static AutoTagTrack ToAutoTagTrack(BeatsourceTrackInfo track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Version = track.Version,
            Artists = track.Artists.ToList(),
            Album = track.Album,
            Key = track.Key,
            Bpm = track.Bpm,
            Genres = track.Genres.ToList(),
            Art = track.Art,
            Url = track.Url,
            Label = track.Label,
            CatalogNumber = track.CatalogNumber,
            TrackId = track.TrackId,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            Remixers = track.Remixers.ToList(),
            ReleaseDate = track.ReleaseDate,
            Isrc = track.Isrc
        };
    }
}
