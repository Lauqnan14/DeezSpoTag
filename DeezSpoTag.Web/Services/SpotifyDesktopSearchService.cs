using System.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyDesktopSearchService
{
    private readonly SpotifySearchService _searchService;

    public SpotifyDesktopSearchService(SpotifySearchService searchService)
    {
        _searchService = searchService;
    }

    public async Task<SpotifyDesktopSearchResponse?> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var result = await _searchService.SearchAsync(query, Math.Clamp(limit, 1, 50), cancellationToken);
        if (result == null)
        {
            return null;
        }

        return new SpotifyDesktopSearchResponse
        {
            Tracks = result.Tracks.Select(MapTrack).ToList(),
            Albums = result.Albums.Select(MapAlbum).ToList(),
            Artists = result.Artists.Select(MapArtist).ToList(),
            Playlists = new List<SpotifyDesktopPlaylist>()
        };
    }

    private static SpotifyDesktopTrack MapTrack(SpotifySearchItem item)
    {
        var (artists, album) = SplitSubtitle(item.Subtitle);
        return new SpotifyDesktopTrack
        {
            Id = item.Id,
            Name = item.Name,
            Type = item.Type,
            Artists = artists,
            AlbumName = album,
            Images = BuildImages(item.ImageUrl),
            ReleaseDate = null,
            SourceUrl = item.SourceUrl,
            DurationMs = item.DurationMs
        };
    }

    private static SpotifyDesktopAlbum MapAlbum(SpotifySearchItem item)
    {
        var (artists, _) = SplitSubtitle(item.Subtitle);
        return new SpotifyDesktopAlbum
        {
            Id = item.Id,
            Name = item.Name,
            Type = item.Type,
            Artists = artists,
            Images = BuildImages(item.ImageUrl),
            ReleaseDate = null,
            SourceUrl = item.SourceUrl,
            TotalTracks = null
        };
    }

    private static SpotifyDesktopArtist MapArtist(SpotifySearchItem item)
    {
        return new SpotifyDesktopArtist
        {
            Id = item.Id,
            Name = item.Name,
            Type = item.Type,
            Images = BuildImages(item.ImageUrl),
            SourceUrl = item.SourceUrl
        };
    }

    private static (string artists, string album) SplitSubtitle(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return (string.Empty, string.Empty);
        }

        var parts = subtitle.Split(" \u2022 ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return (parts[0].Trim(), parts[1].Trim());
        }

        return (subtitle.Trim(), string.Empty);
    }

    private static List<SpotifyDesktopImage> BuildImages(string? url)
    {
        var list = new List<SpotifyDesktopImage>();
        if (string.IsNullOrWhiteSpace(url))
        {
            return list;
        }

        list.Add(new SpotifyDesktopImage { Url = url });
        return list;
    }
}

public sealed class SpotifyDesktopSearchResponse
{
    public List<SpotifyDesktopTrack> Tracks { get; init; } = new();
    public List<SpotifyDesktopAlbum> Albums { get; init; } = new();
    public List<SpotifyDesktopArtist> Artists { get; init; } = new();
    public List<SpotifyDesktopPlaylist> Playlists { get; init; } = new();
}

public sealed class SpotifyDesktopImage
{
    public string? Url { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
}

public sealed class SpotifyDesktopTrack
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "track";
    public string Artists { get; init; } = "";
    public string AlbumName { get; init; } = "";
    public List<SpotifyDesktopImage> Images { get; init; } = new();
    public string? ReleaseDate { get; init; }
    public string? SourceUrl { get; init; }
    public int? DurationMs { get; init; }
}

public sealed class SpotifyDesktopAlbum
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "album";
    public string Artists { get; init; } = "";
    public List<SpotifyDesktopImage> Images { get; init; } = new();
    public string? ReleaseDate { get; init; }
    public string? SourceUrl { get; init; }
    public int? TotalTracks { get; init; }
}

public sealed class SpotifyDesktopArtist
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "artist";
    public List<SpotifyDesktopImage> Images { get; init; } = new();
    public string? SourceUrl { get; init; }
}

public sealed class SpotifyDesktopPlaylist
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "playlist";
    public List<SpotifyDesktopImage> Images { get; init; } = new();
    public string? SourceUrl { get; init; }
    public string? OwnerDisplayName { get; init; }
    public int? TracksTotal { get; init; }
}
