using System.Globalization;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DiscogsMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex ArtistCleanupRegex = new(@" \(\d{1,2}\)$", RegexOptions.Compiled, RegexTimeout);
    private readonly DiscogsClient _client;
    private readonly ILogger<DiscogsMatcher> _logger;
    private readonly Dictionary<long, DiscogsRelease> _releaseCache = new();

    public DiscogsMatcher(DiscogsClient client, ILogger<DiscogsMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AutoTagMatchResult?> MatchAsync(AutoTagAudioInfo info, AutoTagMatchingConfig config, DiscogsConfig discogsConfig, bool matchById, bool needsLabelOrCatalog, CancellationToken cancellationToken)
    {
        ApplyDiscogsConfig(discogsConfig);
        if (!await ValidateDiscogsTokenAsync(discogsConfig, cancellationToken))
        {
            return null;
        }

        var idMatch = await TryMatchByTaggedReleaseIdAsync(info, config, discogsConfig, matchById, cancellationToken);
        if (idMatch != null)
        {
            return idMatch;
        }

        var query = $"{OneTaggerMatching.CleanTitle(info.Title)} {OneTaggerMatching.CleanArtistSearching(info.Artist)}";
        var results = await SearchDiscogsReleasesAsync(query, info, cancellationToken);
        if (results.Count == 0)
        {
            return null;
        }

        results = results.Take(Math.Max(1, discogsConfig.MaxAlbums)).ToList();
        return await MatchSearchResultsAsync(
            info,
            config,
            discogsConfig,
            needsLabelOrCatalog,
            results,
            cancellationToken);
    }

    private void ApplyDiscogsConfig(DiscogsConfig discogsConfig)
    {
        if (!string.IsNullOrWhiteSpace(discogsConfig.Token))
        {
            _client.SetToken(discogsConfig.Token);
        }

        if (discogsConfig.RateLimit.HasValue)
        {
            _client.SetRateLimit(discogsConfig.RateLimit.Value);
        }
    }

    private async Task<bool> ValidateDiscogsTokenAsync(DiscogsConfig discogsConfig, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(discogsConfig.Token))
        {
            return true;
        }

        var valid = await _client.ValidateTokenAsync(cancellationToken);
        if (!valid)
        {
            _logger.LogWarning("Discogs token invalid.");
        }

        return valid;
    }

    private async Task<AutoTagMatchResult?> TryMatchByTaggedReleaseIdAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        DiscogsConfig discogsConfig,
        bool matchById,
        CancellationToken cancellationToken)
    {
        if (!matchById || !info.Tags.TryGetValue("DISCOGS_RELEASE_ID", out var tagValues))
        {
            return null;
        }

        var raw = tagValues.FirstOrDefault();
        var cleaned = raw?.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || !long.TryParse(cleaned, out var releaseId))
        {
            return null;
        }

        var release = await GetReleaseAsync(DiscogsReleaseType.Release, releaseId, cancellationToken);
        if (release == null)
        {
            return null;
        }

        if (info.TrackNumber.HasValue && info.TrackNumber.Value > 0 && info.TrackNumber.Value <= release.Tracks.Count)
        {
            var direct = ToTrack(release, info.TrackNumber.Value - 1, discogsConfig);
            return new AutoTagMatchResult { Accuracy = 1.0, Track = ToAutoTagTrack(direct) };
        }

        var tracks = release.Tracks.Select((_, idx) => ToTrack(release, idx, discogsConfig)).ToList();
        var match = MatchTracks(info, tracks, config, configMatchArtist: false);
        return match == null
            ? null
            : new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(match.Track) };
    }

    private async Task<List<DiscogsSearchResult>> SearchDiscogsReleasesAsync(
        string query,
        AutoTagAudioInfo info,
        CancellationToken cancellationToken)
    {
        var results = await _client.SearchAsync("release,master", query, null, null, cancellationToken);
        if (results.Count > 0)
        {
            return results;
        }

        return await _client.SearchAsync("release,master", null, OneTaggerMatching.CleanTitle(info.Title), info.Artist, cancellationToken);
    }

    private async Task<AutoTagMatchResult?> MatchSearchResultsAsync(
        AutoTagAudioInfo info,
        AutoTagMatchingConfig config,
        DiscogsConfig discogsConfig,
        bool needsLabelOrCatalog,
        IReadOnlyList<DiscogsSearchResult> results,
        CancellationToken cancellationToken)
    {
        foreach (var search in results)
        {
            var release = await GetReleaseAsync(search.Type, search.Id, cancellationToken);
            if (release == null)
            {
                continue;
            }

            var tracks = release.Tracks.Select((_, idx) => ToTrack(release, idx, discogsConfig)).ToList();
            var match = MatchTracks(info, tracks, config, configMatchArtist: false);
            if (match == null)
            {
                continue;
            }

            await PopulateMissingLabelOrCatalogAsync(match.Track, release, needsLabelOrCatalog, cancellationToken);

            return new AutoTagMatchResult { Accuracy = match.Accuracy, Track = ToAutoTagTrack(match.Track) };
        }

        return null;
    }

    private async Task PopulateMissingLabelOrCatalogAsync(
        DiscogsTrackInfo track,
        DiscogsRelease release,
        bool needsLabelOrCatalog,
        CancellationToken cancellationToken)
    {
        if (!needsLabelOrCatalog || !NeedReleaseLabelOrCatalog(track, release) || release.Labels != null || !release.MainRelease.HasValue)
        {
            return;
        }

        var releaseInfo = await GetReleaseAsync(DiscogsReleaseType.Release, release.MainRelease.Value, cancellationToken);
        if (releaseInfo?.Labels?.FirstOrDefault() is not { } label)
        {
            return;
        }

        track.Label = track.Label ?? CleanArtist(label.Name);
        if (string.IsNullOrWhiteSpace(track.CatalogNumber) && !string.IsNullOrWhiteSpace(label.Catno) && label.Catno != "none")
        {
            track.CatalogNumber = label.Catno;
        }
    }

    private async Task<DiscogsRelease?> GetReleaseAsync(DiscogsReleaseType type, long id, CancellationToken cancellationToken)
    {
        if (_releaseCache.TryGetValue(id, out var cached))
        {
            return cached;
        }
        var release = await _client.GetReleaseAsync(type, id, cancellationToken);
        if (release != null)
        {
            _releaseCache[id] = release;
        }
        return release;
    }

    private static bool NeedReleaseLabelOrCatalog(DiscogsTrackInfo track, DiscogsRelease release)
    {
        return (string.IsNullOrWhiteSpace(track.CatalogNumber) || string.IsNullOrWhiteSpace(track.Label))
               && release.Labels == null
               && release.MainRelease.HasValue;
    }

    private static MatchCandidate? MatchTracks(AutoTagAudioInfo info, List<DiscogsTrackInfo> tracks, AutoTagMatchingConfig config, bool configMatchArtist)
    {
        var match = OneTaggerMatching.MatchTrack(
            info,
            tracks,
            config,
            new OneTaggerMatching.TrackSelectors<DiscogsTrackInfo>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                track => track.Duration,
                track => track.ReleaseDate),
            matchArtist: configMatchArtist);

        return match == null ? null : new MatchCandidate(match.Accuracy, match.Track);
    }

    private static DiscogsTrackInfo ToTrack(DiscogsRelease release, int trackIndex, DiscogsConfig config)
    {
        var track = release.Tracks[trackIndex];
        var releaseDate = ParseDate(release.Released);

        string? catalogNumber = null;
        if (release.Labels?.FirstOrDefault() is { } label && !string.IsNullOrWhiteSpace(label.Catno) && label.Catno != "none")
        {
            catalogNumber = label.Catno;
        }

        var position = track.Position;
        var (discNumber, trackNumber) = ResolveTrackPosition(position, trackIndex, config.TrackNumberInt);
        var other = BuildOtherFields(release, position);

        return new DiscogsTrackInfo
        {
            Title = track.Title,
            Artists = track.Artists?.Select(a => CleanArtist(a.Name)).ToList()
                      ?? release.Artists.Select(a => CleanArtist(a.Name)).ToList(),
            AlbumArtists = release.Artists.Select(a => CleanArtist(a.Name)).ToList(),
            Album = release.Title,
            Genres = release.Genres,
            Styles = release.Styles ?? new List<string>(),
            Art = release.Images?.FirstOrDefault()?.Url,
            Url = release.Url,
            Label = release.Labels != null && release.Labels.Count > 0 ? CleanArtist(release.Labels[0].Name) : null,
            ReleaseYear = release.Year,
            ReleaseDate = releaseDate,
            CatalogNumber = catalogNumber,
            ReleaseId = release.Id.ToString(CultureInfo.InvariantCulture),
            Duration = ParseDuration(track.Duration),
            TrackNumber = config.TrackNumberInt ? (int?)int.Parse(trackNumber, CultureInfo.InvariantCulture) : null,
            DiscNumber = discNumber,
            TrackTotal = release.Tracks.Count,
            Other = other
        };
    }

    private static (int? DiscNumber, string TrackNumber) ResolveTrackPosition(string position, int trackIndex, bool trackNumberInt)
    {
        if (!trackNumberInt)
        {
            return (null, position);
        }

        var trackNumber = (trackIndex + 1).ToString(CultureInfo.InvariantCulture);
        int? discNumber = null;
        var match = Regex.Match(position, "(\\d+)(\\.|-)(\\d+)", RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            discNumber = int.TryParse(match.Groups[1].Value, out var disc) ? disc : null;
            trackNumber = match.Groups[3].Value;
        }

        return (discNumber, trackNumber);
    }

    private static List<(string Key, List<string> Values)> BuildOtherFields(DiscogsRelease release, string position)
    {
        var other = new List<(string Key, List<string> Values)>
        {
            ("VINYLTRACK", new List<string> { position })
        };

        if (release.Formats == null)
        {
            return other;
        }

        var formats = release.Formats.Select(format =>
                format.Descriptions != null
                    ? $"{format.Qty} x {format.Name}, {string.Join(", ", format.Descriptions)}"
                    : $"{format.Qty} x {format.Name}")
            .ToList();
        other.Add(("MEDIATYPE", formats));
        return other;
    }

    private static string CleanArtist(string input) => ArtistCleanupRegex.Replace(input, "").Trim();

    private static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        return DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static TimeSpan ParseDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return TimeSpan.Zero;
        var parts = duration.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var minutes) && int.TryParse(parts[1], out var seconds))
        {
            return TimeSpan.FromSeconds((minutes * 60d) + seconds);
        }
        return TimeSpan.Zero;
    }

    private static AutoTagTrack ToAutoTagTrack(DiscogsTrackInfo track)
    {
        return new AutoTagTrack
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            AlbumArtists = track.AlbumArtists.ToList(),
            Album = track.Album,
            Genres = track.Genres.ToList(),
            Styles = track.Styles.ToList(),
            Art = track.Art,
            Url = track.Url,
            Label = track.Label,
            ReleaseDate = track.ReleaseDate,
            CatalogNumber = track.CatalogNumber,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            TrackNumber = track.TrackNumber,
            DiscNumber = track.DiscNumber,
            TrackTotal = track.TrackTotal,
            Other = track.Other.ToDictionary(k => k.Key, v => v.Values)
        };
    }

    private sealed record MatchCandidate(double Accuracy, DiscogsTrackInfo Track);
}

public sealed class DiscogsTrackInfo
{
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string Album { get; set; } = "";
    public List<string> Genres { get; set; } = new();
    public List<string> Styles { get; set; } = new();
    public string? Art { get; set; }
    public string Url { get; set; } = "";
    public string? Label { get; set; }
    public int? ReleaseYear { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? CatalogNumber { get; set; }
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int TrackTotal { get; set; }
    public List<(string Key, List<string> Values)> Other { get; set; } = new();
}
