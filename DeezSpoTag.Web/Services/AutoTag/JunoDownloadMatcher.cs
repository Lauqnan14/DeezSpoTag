namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class JunoDownloadMatcher
{
    private readonly JunoDownloadClient _client;

    public JunoDownloadMatcher(JunoDownloadClient client)
    {
        _client = client;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, CancellationToken cancellationToken)
    {
        var query = $"{info.Artist} {OneTaggerMatching.CleanTitle(info.Title)}";
        var tracks = await _client.SearchAsync(query, cancellationToken);
        if (tracks.Count == 0)
        {
            return null;
        }

        var match = MatchTracks(info, tracks, config);
        if (match == null)
        {
            return null;
        }

        return new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(match.Track) };
    }

    private static MatchCandidate? MatchTracks(AutoTagAudioInfo info, List<JunoDownloadTrackInfo> tracks, AutoTagMatchingConfig config)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<JunoDownloadTrackInfo>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: true);

        return match == null ? null : new MatchCandidate(match.Accuracy, match.Track);
    }

    private static AutoTagTrack ToAutoTagTrack(JunoDownloadTrackInfo track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Bpm = track.Bpm,
            Genres = track.Genres.ToList(),
            Label = track.Label,
            ReleaseDate = track.ReleaseDate,
            Art = track.Art,
            Url = track.Url,
            CatalogNumber = track.CatalogNumber,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal
        };
    }

    private sealed record MatchCandidate(double Accuracy, JunoDownloadTrackInfo Track);
}
