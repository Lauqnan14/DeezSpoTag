using System.Globalization;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class SpotifyClient
{
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly SpotifyMetadataService _metadataService;
    private readonly ILogger<SpotifyClient> _logger;

    public SpotifyClient(
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        SpotifyMetadataService metadataService,
        ILogger<SpotifyClient> logger)
    {
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _metadataService = metadataService;
        _logger = logger;
    }

    public async Task<List<SpotifyTrackInfo>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var summaries = await _pathfinderMetadataClient.SearchTracksAsync(query, limit, cancellationToken);
        if (summaries.Count == 0)
        {
            return new List<SpotifyTrackInfo>();
        }

        try
        {
            summaries = await _metadataService.HydrateTrackIsrcsWithPathfinderAsync(summaries, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify track ISRC hydration failed.");
        }

        try
        {
            summaries = await _metadataService.HydrateTrackAudioFeaturesAsync(summaries, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify track audio feature hydration failed.");
        }

        return summaries.Select(ToTrackInfo).ToList();
    }

    public async Task<SpotifyTrackInfo> EnrichTrackWithPathfinderAsync(SpotifyTrackInfo track, CancellationToken cancellationToken) // NOSONAR
    {
        if (string.IsNullOrWhiteSpace(track.TrackId))
        {
            return track;
        }

        var sourceUrl = string.IsNullOrWhiteSpace(track.Url)
            ? $"https://open.spotify.com/track/{track.TrackId}"
            : track.Url;

        try
        {
            var metadata = await _metadataService.FetchByUrlAsync(sourceUrl, cancellationToken);
            if (metadata == null || metadata.TrackList.Count == 0)
            {
                return track;
            }

            var summary = metadata.TrackList.FirstOrDefault(item =>
                item.Id.Equals(track.TrackId, StringComparison.OrdinalIgnoreCase))
                ?? metadata.TrackList[0];
            ApplySummary(track, summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify pathfinder enrichment failed for {TrackId}.", track.TrackId);
        }

        return track;
    }

    private static SpotifyTrackInfo ToTrackInfo(SpotifyTrackSummary summary)
    {
        var artists = SplitArtists(summary.Artists);

        return new SpotifyTrackInfo
        {
            Title = summary.Name,
            Artists = artists,
            Album = summary.Album,
            AlbumArtist = summary.AlbumArtist,
            Url = summary.SourceUrl,
            TrackId = summary.Id,
            ReleaseId = summary.AlbumId ?? string.Empty,
            Duration = summary.DurationMs.HasValue ? TimeSpan.FromMilliseconds(summary.DurationMs.Value) : TimeSpan.Zero,
            Art = summary.ImageUrl,
            Isrc = summary.Isrc,
            ReleaseDate = TryParseDate(summary.ReleaseDate),
            Explicit = summary.Explicit,
            TrackNumber = summary.TrackNumber,
            DiscNumber = summary.DiscNumber,
            TrackTotal = summary.TrackTotal,
            Label = summary.Label,
            Genres = summary.Genres?.ToList() ?? new List<string>(),
            Danceability = summary.Danceability,
            Energy = summary.Energy,
            Valence = summary.Valence,
            Acousticness = summary.Acousticness,
            Instrumentalness = summary.Instrumentalness,
            Speechiness = summary.Speechiness,
            Loudness = summary.Loudness,
            Tempo = summary.Tempo,
            TimeSignature = summary.TimeSignature,
            Liveness = summary.Liveness,
            Key = SpotifyAudioFeatureMapper.MapKey(summary.Key, summary.Mode)
        };
    }

    private static List<string> SplitArtists(string? artists)
    {
        return artists
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList() ?? new List<string>();
    }

    private static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        if (value.Length == 4 && int.TryParse(value, out var year) && year is > 0 and < 10000)
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }

    private static void ApplySummary(SpotifyTrackInfo track, SpotifyTrackSummary summary)
    {
        var artists = SplitArtists(summary.Artists);
        if (string.IsNullOrWhiteSpace(track.Title))
        {
            track.Title = summary.Name;
        }

        if (artists.Count > 0)
        {
            track.Artists = artists;
        }

        if (string.IsNullOrWhiteSpace(track.Album))
        {
            track.Album = summary.Album;
        }

        if (string.IsNullOrWhiteSpace(track.AlbumArtist))
        {
            track.AlbumArtist = summary.AlbumArtist;
        }

        if (!string.IsNullOrWhiteSpace(summary.AlbumId))
        {
            track.ReleaseId = summary.AlbumId;
        }

        if (track.Duration == TimeSpan.Zero && summary.DurationMs.HasValue)
        {
            track.Duration = TimeSpan.FromMilliseconds(summary.DurationMs.Value);
        }

        if (string.IsNullOrWhiteSpace(track.Art))
        {
            track.Art = summary.ImageUrl;
        }

        if (string.IsNullOrWhiteSpace(track.Isrc))
        {
            track.Isrc = summary.Isrc;
        }

        track.ReleaseDate ??= TryParseDate(summary.ReleaseDate);
        track.Explicit ??= summary.Explicit;
        track.TrackNumber ??= summary.TrackNumber;
        track.DiscNumber ??= summary.DiscNumber;
        track.TrackTotal ??= summary.TrackTotal;

        if (string.IsNullOrWhiteSpace(track.Label))
        {
            track.Label = summary.Label;
        }

        if ((track.Genres == null || track.Genres.Count == 0) && summary.Genres is { Count: > 0 })
        {
            track.Genres = summary.Genres.ToList();
        }

        track.Danceability ??= summary.Danceability;
        track.Energy ??= summary.Energy;
        track.Valence ??= summary.Valence;
        track.Acousticness ??= summary.Acousticness;
        track.Instrumentalness ??= summary.Instrumentalness;
        track.Speechiness ??= summary.Speechiness;
        track.Loudness ??= summary.Loudness;
        track.Tempo ??= summary.Tempo;
        track.TimeSignature ??= summary.TimeSignature;
        track.Liveness ??= summary.Liveness;
        track.Key ??= SpotifyAudioFeatureMapper.MapKey(summary.Key, summary.Mode);
    }
}
