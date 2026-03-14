using System;
using System.Collections.Generic;
using System.Linq;

namespace DeezSpoTag.Web.Services;

public static class SpotifyArtistPagePayloadMapper
{
    private const string SpotifySource = "spotify";
    private const string CompileType = "compile";
    private const string SingleType = "single";
    private const string AlbumType = "album";

    public static Dictionary<string, object> Build(SpotifyArtistPageResult artistPage)
    {
        var profile = artistPage.Artist;
        var albums = artistPage.Albums ?? new List<SpotifyAlbum>();
        var appearsOn = artistPage.AppearsOn ?? new List<SpotifyAlbum>();

        static bool IsCompilation(SpotifyAlbum album)
        {
            var group = (album.AlbumGroup ?? string.Empty).Trim().ToLowerInvariant();
            return group is "compilation" or CompileType;
        }

        static bool IsSinglesOrEp(SpotifyAlbum album)
        {
            var group = (album.AlbumGroup ?? string.Empty).Trim().ToLowerInvariant();
            return group is SingleType or "ep"
                || string.Equals(album.DiscographySection, "singles_eps", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsAlbum(SpotifyAlbum album)
        {
            var group = (album.AlbumGroup ?? string.Empty).Trim().ToLowerInvariant();
            if (group == AlbumType)
            {
                return true;
            }

            return !IsCompilation(album)
                && !IsSinglesOrEp(album)
                && string.Equals(album.DiscographySection, "albums", StringComparison.OrdinalIgnoreCase);
        }

        static List<object> MapDistinctReleases(IEnumerable<SpotifyAlbum> sourceAlbums)
        {
            return sourceAlbums
                .GroupBy(album => album.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => MapSpotifyRelease(group.First()))
                .ToList();
        }

        var popularReleases = MapDistinctReleases(albums.Where(album => album.IsPopular));
        var albumReleases = MapDistinctReleases(albums.Where(IsAlbum));
        var singlesAndEpReleases = MapDistinctReleases(albums.Where(IsSinglesOrEp));
        var compilationReleases = MapDistinctReleases(albums.Where(IsCompilation));
        var featuredReleases = MapDistinctReleases(appearsOn);

        var topTracks = (artistPage.TopTracks ?? new List<SpotifyTrack>())
            .Select(track => new
            {
                id = track.Id,
                title = track.Name,
                duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
                rank = track.Popularity > 0 ? track.Popularity * 10_000 : 0,
                link = track.SourceUrl ?? $"https://open.spotify.com/track/{track.Id}",
                source = SpotifySource,
                release_date = track.ReleaseDate ?? string.Empty,
                explicit_lyrics = track.Explicit ?? false,
                has_lyrics = track.HasLyrics ?? false,
                artist = new
                {
                    id = profile.Id,
                    name = profile.Name,
                    source = SpotifySource
                },
                album = new
                {
                    id = track.AlbumId ?? string.Empty,
                    title = track.AlbumName ?? string.Empty,
                    cover_medium = SelectBestSpotifyImageUrl(track.AlbumImages),
                    source = SpotifySource,
                    type = MapTrackAlbumType(track),
                    release_date = track.ReleaseDate ?? string.Empty
                }
            })
            .ToList();

        var related = (artistPage.RelatedArtists ?? new List<SpotifyRelatedArtist>())
            .Select(artist => new
            {
                id = artist.Id,
                name = artist.Name,
                picture_medium = SelectBestSpotifyImageUrl(artist.Images),
                picture = SelectBestSpotifyImageUrl(artist.Images),
                source = SpotifySource,
                deezer_id = artist.DeezerId ?? string.Empty,
                deezer_url = artist.DeezerUrl ?? string.Empty
            })
            .ToList();

        var releases = new Dictionary<string, object>();
        if (popularReleases.Count > 0)
        {
            releases["popular"] = popularReleases;
        }
        if (albumReleases.Count > 0)
        {
            releases[AlbumType] = albumReleases;
        }
        if (singlesAndEpReleases.Count > 0)
        {
            releases["singles_eps"] = singlesAndEpReleases;
        }
        if (compilationReleases.Count > 0)
        {
            releases[CompileType] = compilationReleases;
        }
        if (featuredReleases.Count > 0)
        {
            releases["featured"] = featuredReleases;
        }

        return new Dictionary<string, object>
        {
            ["id"] = profile.Id,
            ["source"] = SpotifySource,
            ["name"] = profile.Name,
            ["picture_xl"] = profile.HeaderImageUrl ?? SelectBestSpotifyImageUrl(profile.Images),
            ["picture_big"] = SelectBestSpotifyImageUrl(profile.Images),
            ["picture_medium"] = SelectBestSpotifyImageUrl(profile.Images),
            ["genres"] = profile.Genres ?? new List<string>(),
            ["nb_fan"] = profile.Followers,
            ["biography"] = profile.Biography ?? string.Empty,
            ["activity_periods"] = profile.ActivityPeriods ?? new List<SpotifyActivityPeriod>(),
            ["sale_periods"] = profile.SalePeriods ?? new List<SpotifySalePeriod>(),
            ["availability"] = profile.Availability ?? new List<SpotifyAvailabilityInfo>(),
            ["is_portrait_album_cover"] = profile.IsPortraitAlbumCover ?? false,
            ["releases"] = releases,
            ["top_tracks"] = topTracks,
            ["related"] = related,
            ["download_link"] = profile.SourceUrl ?? $"https://open.spotify.com/artist/{profile.Id}"
        };
    }

    private static string MapTrackAlbumType(SpotifyTrack track)
    {
        var normalizedType = (track.ReleaseType ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedType == "SINGLE")
        {
            return "single";
        }
        if (normalizedType == "EP")
        {
            return "ep";
        }
        if (normalizedType == "COMPILATION")
        {
            return CompileType;
        }
        if (normalizedType == "ALBUM")
        {
            return AlbumType;
        }

        var normalizedGroup = (track.AlbumGroup ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedGroup is SingleType or "ep" or AlbumType)
        {
            return normalizedGroup;
        }
        if (normalizedGroup == "compilation")
        {
            return CompileType;
        }

        if (track.AlbumTrackTotal.HasValue && track.AlbumTrackTotal.Value == 1)
        {
            return SingleType;
        }

        return AlbumType;
    }

    private static object MapSpotifyRelease(SpotifyAlbum album)
    {
        return new
        {
            id = album.Id,
            title = album.Name,
            cover_medium = SelectBestSpotifyImageUrl(album.Images),
            release_date = album.ReleaseDate,
            nb_tracks = album.TotalTracks,
            record_type = string.IsNullOrWhiteSpace(album.AlbumGroup) ? "album" : album.AlbumGroup,
            explicit_lyrics = false,
            link = album.SourceUrl ?? $"https://open.spotify.com/album/{album.Id}",
            source = "spotify",
            available_markets = album.AvailableMarkets ?? Array.Empty<string>(),
            release_date_precision = album.ReleaseDatePrecision ?? string.Empty,
            copyright = album.CopyrightText ?? string.Empty,
            review = album.Review ?? string.Empty,
            related_album_ids = album.RelatedAlbumIds ?? Array.Empty<string>(),
            original_title = album.OriginalTitle ?? string.Empty,
            version_title = album.VersionTitle ?? string.Empty,
            sale_periods = album.SalePeriods ?? Array.Empty<SpotifySalePeriod>(),
            availability = album.Availability ?? Array.Empty<SpotifyAvailabilityInfo>(),
            genres = album.Genres ?? Array.Empty<string>(),
            label = album.Label ?? string.Empty,
            popularity = album.Popularity ?? 0
        };
    }

    private static string SelectBestSpotifyImageUrl(IEnumerable<SpotifyImage>? images)
    {
        if (images is null)
        {
            return string.Empty;
        }

        return images
            .OrderByDescending(image => image.Width ?? 0)
            .ThenByDescending(image => image.Height ?? 0)
            .Select(image => image.Url)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
            ?? string.Empty;
    }
}
