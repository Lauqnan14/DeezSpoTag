using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Metadata;
using DeezerSdkClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Web.Services;

public sealed class DeezerMetadataResolver : IMetadataResolver
{
    private static readonly Regex DeezerTrackUrlRegex = new(
        @"deezer\.com/(?:[a-z]{2}/)?track/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private readonly DeezerSdkClient _deezerClient;
    private readonly ILogger<DeezerMetadataResolver> _logger;

    public DeezerMetadataResolver(DeezerSdkClient deezerClient, ILogger<DeezerMetadataResolver> logger)
    {
        _deezerClient = deezerClient;
        _logger = logger;
    }

    public string SourceKey => "deezer";

    public async Task ResolveTrackAsync(Track track, DeezSpoTagSettings settings, CancellationToken cancellationToken)
    {
        var deezerTrack = await ResolveDeezerTrackAsync(track, cancellationToken);
        if (deezerTrack == null || string.IsNullOrWhiteSpace(deezerTrack.Id) || deezerTrack.Id == "0")
        {
            return;
        }

        ApiAlbum? deezerAlbum = null;
        if (deezerTrack.Album?.Id > 0)
        {
            try
            {
                deezerAlbum = await _deezerClient.GetAlbumAsync(deezerTrack.Album.Id.ToString());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed loading Deezer album metadata for track {TrackId}", deezerTrack.Id);
                }
            }
        }

        ApplyMetadata(track, deezerTrack, deezerAlbum, settings);
    }

    private async Task<ApiTrack?> ResolveDeezerTrackAsync(Track track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var byDirectId = await TryResolveByDirectIdAsync(track, cancellationToken);
        if (byDirectId != null)
        {
            return byDirectId;
        }

        var byIsrc = await TryResolveByIsrcAsync(track.ISRC);
        if (byIsrc != null)
        {
            return byIsrc;
        }

        var artist = track.MainArtist?.Name ?? track.ArtistString;
        var title = track.Title;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return await TryResolveByMetadataAsync(track, artist, title, cancellationToken);
    }

    private async Task<ApiTrack?> TryResolveByDirectIdAsync(Track track, CancellationToken cancellationToken)
    {
        var directId = FirstNonEmpty(
            TryNormalizeTrackId(track.SourceId),
            TryNormalizeTrackId(track.Urls.GetValueOrDefault("deezer_track_id")),
            TryNormalizeTrackId(track.Urls.GetValueOrDefault("deezer_id")),
            TryNormalizeTrackId(track.Urls.GetValueOrDefault("deezerid")),
            TryNormalizeTrackId(track.Urls.GetValueOrDefault("deezer")),
            TryNormalizeTrackId(track.DownloadURL));
        if (string.IsNullOrWhiteSpace(directId))
        {
            return null;
        }

        return await TryGetTrackAsync(directId, cancellationToken);
    }

    private async Task<ApiTrack?> TryResolveByIsrcAsync(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            var byIsrc = await _deezerClient.GetTrackByIsrcAsync(isrc);
            return byIsrc != null && !string.IsNullOrWhiteSpace(byIsrc.Id) && byIsrc.Id != "0"
                ? byIsrc
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer ISRC lookup failed for {Isrc}", isrc);
            }
            return null;
        }
    }

    private async Task<ApiTrack?> TryResolveByMetadataAsync(
        Track track,
        string artist,
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            var durationMs = track.Duration > 0 ? track.Duration * 1000 : (int?)null;
            var deezerTrackId = await _deezerClient.GetTrackIdFromMetadataAsync(
                artist,
                title,
                track.Album?.Title ?? string.Empty,
                durationMs);
            if (string.IsNullOrWhiteSpace(deezerTrackId) || deezerTrackId == "0")
            {
                return null;
            }

            return await TryGetTrackAsync(deezerTrackId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer metadata lookup failed for {Artist} - {Title}", artist, title);
            }
            return null;
        }
    }

    private async Task<ApiTrack?> TryGetTrackAsync(string trackId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var track = await _deezerClient.GetTrackAsync(trackId);
            return track != null && !string.IsNullOrWhiteSpace(track.Id) && track.Id != "0" ? track : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer track lookup failed for {TrackId}", trackId);
            }
            return null;
        }
    }

    private static void ApplyMetadata(Track track, ApiTrack deezerTrack, ApiAlbum? deezerAlbum, DeezSpoTagSettings settings)
    {
        ApplyTrackFields(track, deezerTrack);
        ApplyArtistFields(track, deezerTrack.Artist);
        EnsureAlbum(track, deezerTrack);
        var album = track.Album ?? new Album("0", string.Empty);
        track.Album = album;
        ApplyReleaseDate(track, album, deezerTrack, deezerAlbum, settings);

        if (deezerAlbum is not null)
        {
            ApplyAlbumDetails(album, deezerAlbum);
        }

        ApplyAlbumArtwork(album, deezerAlbum, deezerTrack);
        ApplyAlbumArtist(album, track.MainArtist);
    }

    private static void ApplyTrackFields(Track track, ApiTrack deezerTrack)
    {
        if (!string.IsNullOrWhiteSpace(deezerTrack.TitleShort))
        {
            track.Title = deezerTrack.TitleShort;
        }
        else if (!string.IsNullOrWhiteSpace(deezerTrack.Title))
        {
            track.Title = deezerTrack.Title;
        }

        if (!string.IsNullOrWhiteSpace(deezerTrack.Isrc))
        {
            track.ISRC = deezerTrack.Isrc;
        }

        if (deezerTrack.Duration > 0)
        {
            track.Duration = deezerTrack.Duration;
        }

        if (deezerTrack.TrackPosition > 0)
        {
            track.TrackNumber = deezerTrack.TrackPosition;
        }

        if (deezerTrack.DiskNumber > 0)
        {
            track.DiscNumber = deezerTrack.DiskNumber;
        }

        if (deezerTrack.Bpm > 0)
        {
            track.Bpm = deezerTrack.Bpm;
        }

        if (!string.IsNullOrWhiteSpace(deezerTrack.Copyright))
        {
            track.Copyright = deezerTrack.Copyright!;
        }

        track.Explicit = deezerTrack.ExplicitLyrics || deezerTrack.ExplicitContentLyrics == 1;

        if (!string.IsNullOrWhiteSpace(deezerTrack.Id))
        {
            track.Urls["deezer_track_id"] = deezerTrack.Id;
            track.Urls["deezer"] = $"https://www.deezer.com/track/{deezerTrack.Id}";
        }
    }

    private static void ApplyArtistFields(Track track, ApiArtist? deezerArtist)
    {
        var artistName = deezerArtist?.Name;
        if (deezerArtist == null || string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        track.MainArtist = new Artist(deezerArtist.Id, artistName);
        track.Artists = new List<string> { artistName };
        track.Artist["Main"] = new List<string> { artistName };
    }

    private static void EnsureAlbum(Track track, ApiTrack deezerTrack)
    {
        var albumId = deezerTrack.Album?.Id > 0 ? deezerTrack.Album.Id.ToString() : "0";
        var albumTitle = !string.IsNullOrWhiteSpace(deezerTrack.Album?.Title)
            ? deezerTrack.Album.Title
            : track.Album?.Title ?? string.Empty;
        track.Album ??= new Album(albumId, albumTitle);

        if (!string.IsNullOrWhiteSpace(albumTitle))
        {
            track.Album.Title = albumTitle;
        }

        if (!string.IsNullOrWhiteSpace(albumId) && albumId != "0")
        {
            track.Album.Id = albumId;
        }
    }

    private static void ApplyReleaseDate(
        Track track,
        Album album,
        ApiTrack deezerTrack,
        ApiAlbum? deezerAlbum,
        DeezSpoTagSettings settings)
    {
        var releaseDate = FirstNonEmpty(deezerTrack.PhysicalReleaseDate, deezerTrack.ReleaseDate, deezerAlbum?.ReleaseDate);
        if (string.IsNullOrWhiteSpace(releaseDate))
        {
            return;
        }

        var parsed = CustomDate.FromString(releaseDate);
        if (string.IsNullOrWhiteSpace(parsed.Year))
        {
            return;
        }

        track.Date = parsed;
        track.DateString = parsed.Format(settings.DateFormat);
        album.Date = parsed;
        album.DateString = parsed.Format(settings.DateFormat);
    }

    private static void ApplyAlbumDetails(Album album, ApiAlbum deezerAlbum)
    {
        if (deezerAlbum.NbTracks.HasValue && deezerAlbum.NbTracks.Value > 0)
        {
            album.TrackTotal = deezerAlbum.NbTracks.Value;
        }

        if (deezerAlbum.NbDisk.HasValue && deezerAlbum.NbDisk.Value > 0)
        {
            album.DiscTotal = deezerAlbum.NbDisk.Value;
        }

        if (!string.IsNullOrWhiteSpace(deezerAlbum.Label))
        {
            album.Label = deezerAlbum.Label;
        }

        if (!string.IsNullOrWhiteSpace(deezerAlbum.Upc))
        {
            album.Barcode = deezerAlbum.Upc;
            album.UPC = deezerAlbum.Upc;
        }

        if (!string.IsNullOrWhiteSpace(deezerAlbum.Copyright))
        {
            album.Copyright = deezerAlbum.Copyright;
        }

        if (deezerAlbum.Genres?.Data?.Count > 0)
        {
            album.Genre = deezerAlbum.Genres.Data
                .Select(genre => genre.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static void ApplyAlbumArtwork(Album album, ApiAlbum? deezerAlbum, ApiTrack deezerTrack)
    {
        var artMd5 = FirstNonEmpty(deezerAlbum?.Md5Image, deezerTrack.Album?.Md5Image, deezerTrack.Md5Image);
        if (string.IsNullOrWhiteSpace(artMd5))
        {
            return;
        }

        album.Md5Image = artMd5;
        album.Pic ??= new Picture(artMd5, "cover");
        album.Pic.Md5 = artMd5;
        album.Pic.Type = "cover";
    }

    private static void ApplyAlbumArtist(Album album, Artist? mainArtist)
    {
        if (mainArtist == null)
        {
            return;
        }

        album.MainArtist = mainArtist;
        album.Artist["Main"] = new List<string> { mainArtist.Name };
        album.Artists = new List<string> { mainArtist.Name };
    }

    private static string? TryNormalizeTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (long.TryParse(candidate, out var id) && id > 0)
        {
            return id.ToString();
        }

        var match = DeezerTrackUrlRegex.Match(candidate);
        if (match.Success && long.TryParse(match.Groups[1].Value, out id) && id > 0)
        {
            return id.ToString();
        }

        const string deezerTrackPrefix = "deezer:track:";
        if (candidate.StartsWith(deezerTrackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rawId = candidate[deezerTrackPrefix.Length..];
            if (long.TryParse(rawId, out id) && id > 0)
            {
                return id.ToString();
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Select(static value => value?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
