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

        return AutoTagMatchSelection.BuildMatchResult(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<JunoDownloadTrackInfo>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            ToAutoTagTrack,
            matchArtist: true);
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

}
