using System.Linq;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Metadata;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyMetadataResolver : IMetadataResolver
{
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly SpotifyMetadataService _metadataService;

    public SpotifyMetadataResolver(
        ISpotifyIdResolver spotifyIdResolver,
        SpotifyMetadataService metadataService,
        ILogger<SpotifyMetadataResolver> logger)
    {
        _spotifyIdResolver = spotifyIdResolver;
        _metadataService = metadataService;
        _ = logger;
    }

    public string SourceKey => "spotify";

    public async Task ResolveTrackAsync(Track track, DeezSpoTagSettings settings, CancellationToken cancellationToken)
    {
        var spotifyId = await ResolveSpotifyIdAsync(track, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return;
        }

        var metadata = await _metadataService.FetchByUrlAsync($"https://open.spotify.com/track/{spotifyId}", cancellationToken);
        var summary = metadata?.TrackList?.FirstOrDefault();
        if (summary == null)
        {
            return;
        }

        ApplySummary(track, summary);
    }

    private async Task<string?> ResolveSpotifyIdAsync(Track track, CancellationToken cancellationToken)
    {
        var title = track.Title ?? string.Empty;
        var artist = track.MainArtist?.Name ?? track.ArtistString ?? string.Empty;
        var album = track.Album?.Title;
        return await _spotifyIdResolver.ResolveTrackIdAsync(title, artist, album, track.ISRC, cancellationToken);
    }

    private static void ApplySummary(Track track, SpotifyTrackSummary summary)
    {
        ApplyTitleAndIsrc(track, summary);
        var artistName = ResolveArtistName(track, summary);
        ApplyArtist(track, artistName);
        ApplyAlbum(track, summary, artistName);
        ApplyExplicit(track, summary);
        ApplyLabelAndGenres(track, summary);
        ApplyCopyright(track, summary);
        ApplyAudioFeatures(track, summary);
    }

    private static void ApplyTitleAndIsrc(Track track, SpotifyTrackSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.Name))
        {
            track.Title = summary.Name;
        }

        if (!string.IsNullOrWhiteSpace(summary.Isrc))
        {
            track.ISRC = summary.Isrc;
        }
    }

    private static string? ResolveArtistName(Track track, SpotifyTrackSummary summary)
    {
        return summary.Artists ?? track.MainArtist?.Name ?? track.ArtistString;
    }

    private static void ApplyArtist(Track track, string? artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            track.MainArtist = new Artist(artistName);
            track.Artist["Main"] = new List<string> { artistName };
            track.Artists = new List<string> { artistName };
        }
    }

    private static void ApplyAlbum(Track track, SpotifyTrackSummary summary, string? artistName)
    {
        var albumTitle = summary.Album ?? track.Album?.Title;
        if (!string.IsNullOrWhiteSpace(albumTitle))
        {
            track.Album ??= new Album(albumTitle);
            track.Album.Title = albumTitle;
            if (!string.IsNullOrWhiteSpace(artistName))
            {
                track.Album.MainArtist = new Artist(artistName);
                track.Album.Artist["Main"] = new List<string> { artistName };
                track.Album.Artists = new List<string> { artistName };
            }
        }
    }

    private static void ApplyExplicit(Track track, SpotifyTrackSummary summary)
    {
        if (summary.Explicit.HasValue)
        {
            track.Explicit = summary.Explicit.Value;
            if (track.Album != null && !track.Album.Explicit.HasValue)
            {
                track.Album.Explicit = summary.Explicit.Value;
            }
        }
    }

    private static void ApplyLabelAndGenres(Track track, SpotifyTrackSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.Label))
        {
            track.Album ??= new Album(summary.Album ?? track.Album?.Title ?? string.Empty);
            track.Album.Label = summary.Label;
        }

        if (summary.Genres is { Count: > 0 })
        {
            track.Album ??= new Album(summary.Album ?? track.Album?.Title ?? string.Empty);
            track.Album.Genre = summary.Genres
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static void ApplyCopyright(Track track, SpotifyTrackSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.CopyrightText))
        {
            track.Copyright = summary.CopyrightText;
            if (track.Album != null && string.IsNullOrWhiteSpace(track.Album.Copyright))
            {
                track.Album.Copyright = summary.CopyrightText;
            }
        }
    }

    private static void ApplyAudioFeatures(Track track, SpotifyTrackSummary summary)
    {
        if (summary.Danceability.HasValue)
        {
            track.Danceability = summary.Danceability;
        }
        if (summary.Energy.HasValue)
        {
            track.Energy = summary.Energy;
        }
        if (summary.Valence.HasValue)
        {
            track.Valence = summary.Valence;
        }
        if (summary.Acousticness.HasValue)
        {
            track.Acousticness = summary.Acousticness;
        }
        if (summary.Instrumentalness.HasValue)
        {
            track.Instrumentalness = summary.Instrumentalness;
        }
        if (summary.Speechiness.HasValue)
        {
            track.Speechiness = summary.Speechiness;
        }
        if (summary.Loudness.HasValue)
        {
            track.Loudness = summary.Loudness;
        }
        if (summary.Tempo.HasValue)
        {
            track.Tempo = summary.Tempo;
            track.Bpm = summary.Tempo.Value;
        }
        if (summary.TimeSignature.HasValue)
        {
            track.TimeSignature = summary.TimeSignature;
        }
        if (summary.Liveness.HasValue)
        {
            track.Liveness = summary.Liveness;
        }
        var mappedKey = SpotifyAudioFeatureMapper.MapKey(summary.Key, summary.Mode);
        if (!string.IsNullOrWhiteSpace(mappedKey))
        {
            track.Key = mappedKey;
        }
    }
}
