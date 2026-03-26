namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagMatchSelection
{
    public static (double Accuracy, TTrack Track)? SelectBestTrack<TTrack>(
        AutoTagAudioInfo info,
        List<TTrack> tracks,
        AutoTagMatchingConfig config,
        OneTaggerMatching.TrackSelectors<TTrack> selectors,
        bool matchArtist = true)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            selectors,
            matchArtist);

        return match == null ? null : (match.Accuracy, match.Track);
    }

    public static AutoTagMatchResult? BuildMatchResult<TTrack>(
        AutoTagAudioInfo info,
        List<TTrack> tracks,
        AutoTagMatchingConfig config,
        OneTaggerMatching.TrackSelectors<TTrack> selectors,
        Func<TTrack, AutoTagTrack> converter,
        bool matchArtist = true)
    {
        var match = SelectBestTrack(info, tracks, config, selectors, matchArtist);
        if (match == null)
        {
            return null;
        }

        return new AutoTagMatchResult
        {
            Accuracy = match.Value.Accuracy,
            Track = converter(match.Value.Track)
        };
    }
}
