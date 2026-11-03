namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagMatchSelection
{
    internal static OneTaggerMatching.MatchSelection<T>? MatchPreferredTrack<T>(
        AutoTagAudioInfo info, List<T> tracks, AutoTagMatchingConfig config,
        OneTaggerMatching.TrackSelectors<T> selectors,
        Func<T, string?> releaseType, Func<T, int?> trackTotal)
    {
        var preferred = tracks.Where(track => AutoTagReleaseCategory.MatchesPreference(
            releaseType(track), trackTotal(track), config.PreferredReleaseType)).ToList();
        var selected = OneTaggerMatching.MatchTrack(info, preferred, config, selectors, matchArtist: true);
        if (selected is not null || !config.PreferAvailableRelease) return selected;
        // Apply the same recording guards to the remaining releases. Preference never
        // manufactures a release or makes a nonmatching recording eligible.
        return OneTaggerMatching.MatchTrack(info, tracks, config, selectors, matchArtist: true);
    }

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
