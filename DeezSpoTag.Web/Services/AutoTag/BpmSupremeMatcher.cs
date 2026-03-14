using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BpmSupremeMatcher
{
    private readonly BpmSupremeClient _client;
    private readonly ILogger<BpmSupremeMatcher> _logger;
    private static readonly Regex TitleSuffixRegex = new(
        " \\(.*\\)$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    public BpmSupremeMatcher(BpmSupremeClient client, ILogger<BpmSupremeMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, BpmSupremeConfig bpmConfig, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bpmConfig.Email) || string.IsNullOrWhiteSpace(bpmConfig.Password))
        {
            _logger.LogWarning("BPM Supreme credentials missing; skipping match.");
            return null;
        }

        string sessionToken;
        try
        {
            sessionToken = await _client.LoginAsync(bpmConfig.Email, bpmConfig.Password, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "BPM Supreme login failed.");
            return null;
        }
        var title = TitleSuffixRegex.Replace(OneTaggerMatching.CleanTitle(info.Title), string.Empty);
        var query = $"{title} {OneTaggerMatching.CleanTitle(info.Artist)}";

        var tracks = await _client.SearchAsync(query, bpmConfig.Library, sessionToken, cancellationToken);
        if (tracks.Count == 0)
        {
            return null;
        }

        var candidates = tracks.SelectMany(t => t.ToTracks()).ToList();
        var match = MatchTracks(info, candidates, config);
        if (match == null)
        {
            return null;
        }

        return new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(match.Track) };
    }

    private static MatchCandidate? MatchTracks(AutoTagAudioInfo info, List<BpmSupremeTrackInfo> tracks, AutoTagMatchingConfig config)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<BpmSupremeTrackInfo>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                _ => null,
                track => track.ReleaseDate),
            matchArtist: true);

        return match == null ? null : new MatchCandidate(match.Accuracy, match.Track);
    }

    private sealed record MatchCandidate(double Accuracy, BpmSupremeTrackInfo Track);

    private static AutoTagTrack ToAutoTagTrack(BpmSupremeTrackInfo track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            Bpm = track.Bpm,
            Genres = track.Genres.ToList(),
            Key = track.Key,
            Label = track.Label,
            ReleaseDate = track.ReleaseDate,
            TrackId = track.TrackId,
            Mood = track.Mood,
            Url = track.Url,
            CatalogNumber = track.CatalogNumber,
            Art = track.Art
        };
    }
}
