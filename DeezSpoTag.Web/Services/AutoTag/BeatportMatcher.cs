using System.Globalization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatportMatcher
{
    private readonly BeatportClient _client;
    private readonly ILogger<BeatportMatcher> _logger;

    public BeatportMatcher(BeatportClient client, ILogger<BeatportMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, BeatportMatchConfig beatportConfig, bool includeReleaseMeta, bool matchById, CancellationToken cancellationToken)
    {
        var byId = await MatchByEmbeddedIdAsync(info, beatportConfig, includeReleaseMeta, matchById, cancellationToken);
        if (byId != null)
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(info.Isrc))
        {
            var isrcMatch = await MatchByIsrcAsync(info.Isrc!, beatportConfig, includeReleaseMeta, cancellationToken);
            if (isrcMatch != null)
            {
                return isrcMatch;
            }
        }

        return await MatchBySearchAsync(info, config, beatportConfig, includeReleaseMeta, cancellationToken);
    }

    private async Task<AutoTagMatchResult?> MatchByEmbeddedIdAsync(
        AutoTagAudioInfo info,
        BeatportMatchConfig beatportConfig,
        bool includeReleaseMeta,
        bool matchById,
        CancellationToken cancellationToken)
    {
        if (!matchById || !info.Tags.TryGetValue("BEATPORT_TRACK_ID", out var tagValues))
        {
            return null;
        }

        var raw = tagValues.FirstOrDefault();
        var cleaned = raw?.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        var full = await ResolveFullTrackAsync(cleaned, beatportConfig, includeReleaseMeta, cancellationToken);
        return full == null
            ? null
            : new AutoTagMatchResult { Accuracy = 1.0, Track = ToAutoTagTrack(full) };
    }

    private async Task<AutoTagMatchResult?> MatchBySearchAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        BeatportMatchConfig beatportConfig,
        bool includeReleaseMeta,
        CancellationToken cancellationToken)
    {
        var query = $"{OneTaggerMatching.CleanArtistSearching(info.Artist)} {OneTaggerMatching.CleanTitle(info.Title)}";
        for (var page = 1; page <= Math.Max(1, beatportConfig.MaxPages); page++)
        {
            var results = await _client.SearchAsync(query, page, 25, cancellationToken);
            if (results?.Data == null || results.Data.Count == 0)
            {
                continue;
            }

            var candidates = results.Data
                .Select(r => ToTrack(r, !beatportConfig.IgnoreVersion))
                .ToList();
            var pageMatch = await MatchSearchCandidatesAsync(
                info,
                candidates,
                config,
                beatportConfig,
                includeReleaseMeta,
                cancellationToken);
            if (pageMatch != null)
            {
                return pageMatch;
            }
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> MatchSearchCandidatesAsync(
        AutoTagAudioInfo info,
        List<BeatportTrackInfo> candidates,
        AutoTagMatchingConfig config,
        BeatportMatchConfig beatportConfig,
        bool includeReleaseMeta,
        CancellationToken cancellationToken)
    {
        while (candidates.Count > 0)
        {
            var match = MatchTracks(info, candidates, config);
            if (match == null)
            {
                return null;
            }

            var trackId = match.Track.TrackId;
            if (!string.IsNullOrWhiteSpace(trackId))
            {
                var full = await ResolveFullTrackAsync(trackId, beatportConfig, includeReleaseMeta, cancellationToken);
                if (full != null)
                {
                    return new AutoTagMatchResult
                    {
                        Accuracy = match.Accuracy,
                        Track = ToAutoTagTrack(full)
                    };
                }
            }

            candidates = candidates.Where(t => t.TrackId != match.Track.TrackId).ToList();
        }

        return null;
    }

    private async Task<AutoTagMatchResult?> MatchByIsrcAsync(string isrc, BeatportMatchConfig beatportConfig, bool includeReleaseMeta, CancellationToken cancellationToken)
    {
        var results = await _client.SearchAsync(isrc, 1, 25, cancellationToken);
        var first = results?.Data?.FirstOrDefault();
        if (first == null)
        {
            return null;
        }

        var full = await ResolveFullTrackAsync(first.TrackId.ToString(CultureInfo.InvariantCulture), beatportConfig, includeReleaseMeta, cancellationToken);
        if (full == null)
        {
            return null;
        }

        return new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = ToAutoTagTrack(full)
        };
    }

    private async Task<BeatportTrackInfo?> ResolveFullTrackAsync(string trackId, BeatportMatchConfig config, bool includeReleaseMeta, CancellationToken cancellationToken)
    {
        if (!long.TryParse(trackId, out var id))
        {
            return null;
        }

        var apiTrack = await _client.GetTrackAsync(id, cancellationToken);
        if (apiTrack == null)
        {
            return null;
        }

        var info = ToTrackInfo(apiTrack, config.ArtResolution);
        if (includeReleaseMeta && info.ReleaseId.Length > 0)
        {
            try
            {
                var release = await _client.GetReleaseAsync(long.Parse(info.ReleaseId, CultureInfo.InvariantCulture), cancellationToken);
                if (release != null)
                {
                    info.TrackTotal = release.TrackCount;
                    info.AlbumArtists = release.Artists?.Select(a => a.Name).ToList() ?? new List<string>();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Beatport release lookup failed.");
            }
        }

        return info;
    }

    private static BeatportTrackInfo ToTrack(BeatportTrackResult result, bool includeVersion)
    {
        return new BeatportTrackInfo
        {
            Title = result.TrackName,
            TrackId = result.TrackId.ToString(CultureInfo.InvariantCulture),
            Artists = result.Artists?.Select(a => a.ArtistName).ToList() ?? new List<string>(),
            Version = includeVersion ? result.MixName : null,
            Duration = TimeSpan.FromMilliseconds(result.Length ?? 0),
            Isrc = result.Isrc
        };
    }

    private static BeatportTrackInfo ToTrackInfo(BeatportTrack track, int artResolution)
    {
        var info = new BeatportTrackInfo
        {
            Title = track.Name,
            Version = track.MixName,
            Artists = track.Artists.Select(a => a.Name).ToList(),
            Album = track.Release.Name,
            Key = track.Key?.Name.Replace(" Major", "", StringComparison.OrdinalIgnoreCase).Replace(" Minor", "m", StringComparison.OrdinalIgnoreCase),
            Bpm = track.Bpm,
            Genres = new List<string> { track.Genre.Name },
            Styles = track.SubGenre != null ? new List<string> { track.SubGenre.Name } : new List<string>(),
            Art = BeatportClient.GetArt(track.Release, artResolution),
            Url = $"https://www.beatport.com/track/{track.Slug}/{track.Id}",
            Label = track.Release.Label.Name,
            CatalogNumber = track.CatalogNumber,
            TrackId = track.Id.ToString(CultureInfo.InvariantCulture),
            ReleaseId = track.Release.Id.ToString(CultureInfo.InvariantCulture),
            Duration = TimeSpan.FromMilliseconds(track.LengthMs ?? 0),
            Remixers = track.Remixers.Select(r => r.Name).ToList(),
            TrackNumber = track.Number.HasValue ? (int)track.Number.Value : null,
            Isrc = track.Isrc,
            ReleaseYear = ParseYear(track.NewReleaseDate),
            PublishYear = ParseYear(track.PublishDate),
            ReleaseDate = ParseDate(track.NewReleaseDate),
            PublishDate = ParseDate(track.PublishDate)
        };

        info.Other.Add(("UNIQUEFILEID", new List<string> { $"https://beatport.com|{track.Id}" }));
        if (track.Exclusive)
        {
            info.Other.Add(("BEATPORT_EXCLUSIVE", new List<string> { "1" }));
        }

        return info;
    }

    private static AutoTagMatchResult? MatchTracks(AutoTagAudioInfo info, List<BeatportTrackInfo> tracks, AutoTagMatchingConfig config)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<BeatportTrackInfo>(
                track => track.Title,
                track => track.Version,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate ?? track.PublishDate),
            matchArtist: true);

        if (match == null)
        {
            return null;
        }

        return new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(match.Track) };
    }

    private static string ParseYearString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 4)
        {
            return "";
        }
        return input.Substring(0, 4);
    }

    private static int? ParseYear(string? input)
    {
        var yearText = ParseYearString(input);
        return int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    private static DateTime? ParseDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (DateTime.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static AutoTagTrack ToAutoTagTrack(BeatportTrackInfo track)
        => AutoTagTrackFactory.FromBeatport(track);
}
