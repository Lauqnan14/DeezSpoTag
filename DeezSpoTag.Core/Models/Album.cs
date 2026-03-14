using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Models.Qobuz;

namespace DeezSpoTag.Core.Models;

public class Album
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string? QobuzId { get; set; }
    public string? Md5Image { get; set; }
    public Artist? MainArtist { get; set; }
    public Dictionary<string, List<string>> Artist { get; set; } = new();
    public List<string> Artists { get; set; } = new();
    public int TrackTotal { get; set; }
    public string? RecordType { get; set; }
    public Picture? Pic { get; set; }
    public CustomDate Date { get; set; } = new();
    public string DateString { get; set; } = "";
    public Artist? VariousArtists { get; set; }
    public List<Track> Tracks { get; set; } = new();
    public Artist? RootArtist { get; set; }
    public List<string> Genre { get; set; } = new();
    public int? DiscTotal { get; set; }
    public string? Label { get; set; }
    public string? Barcode { get; set; }
    public string? UPC { get; set; }
    public bool? Explicit { get; set; }
    public int? Bitrate { get; set; }
    public string? EmbeddedCoverPath { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Copyright { get; set; }
    public QobuzQualityInfo? QobuzQuality { get; set; }

    public Album(string id, string title, string? md5Image = null)
    {
        Id = id;
        Title = title;
        Md5Image = md5Image;
    }

    public Album(string title)
    {
        Id = "";
        Title = title;
    }

    public void MakePlaylistCompilation(Playlist playlist)
    {
        Title = playlist.Title;
        MainArtist = new Artist("Various Artists");
        Artist["Main"] = new List<string> { "Various Artists" };
        Artists = new List<string> { "Various Artists" };
        TrackTotal = playlist.TrackTotal;
        RecordType = "compile";
        Pic = playlist.Pic;
        Date = playlist.Date;
    }

    public string GetCleanTitle()
    {
        return Title; // Placeholder
    }

    public void ParseAlbum(ApiAlbum albumAPI)
    {
        UpdateTitle(albumAPI);
        MainArtist = BuildMainArtist(albumAPI);
        EnsureMainArtistBucket();
        ParseContributors(albumAPI);
        TrackTotal = albumAPI.NbTracks ?? 0;
        RecordType = albumAPI.RecordType ?? RecordType;
        Barcode = albumAPI.Upc ?? Barcode;
        Label = albumAPI.Label ?? Label;
        Explicit = albumAPI.ExplicitLyrics ?? false;
        ApplyReleaseDate(albumAPI);
        DiscTotal = albumAPI.NbDisk ?? 1;
        Copyright = albumAPI.Copyright ?? "";
        UpdatePicture(albumAPI);
        EnsurePictureType();
        ParseGenres(albumAPI);
    }

    private void UpdateTitle(ApiAlbum albumAPI)
    {
        if (!string.IsNullOrWhiteSpace(albumAPI.Title))
        {
            Title = albumAPI.Title;
        }
    }

    private static Artist BuildMainArtist(ApiAlbum albumAPI)
    {
        return new Artist(
            albumAPI.Artist?.Id ?? 0,
            albumAPI.Artist?.Name ?? "",
            "Main",
            ExtractArtistPictureMd5(albumAPI.Artist?.PictureSmall));
    }

    private void EnsureMainArtistBucket()
    {
        if (!Artist.ContainsKey("Main"))
        {
            Artist["Main"] = new List<string>();
        }
    }

    private static string ExtractArtistPictureMd5(string? pictureSmall)
    {
        var artistPicture = pictureSmall ?? string.Empty;
        if (string.IsNullOrEmpty(artistPicture))
        {
            return artistPicture;
        }

        var artistIndex = artistPicture.IndexOf("artist/", StringComparison.Ordinal);
        return artistIndex >= 0
            ? artistPicture.Substring(artistIndex + 7, artistPicture.Length - (artistIndex + 7) - 24)
            : artistPicture;
    }

    private void ParseContributors(ApiAlbum albumAPI)
    {
        if (albumAPI.Contributors == null)
        {
            return;
        }

        foreach (var contributor in albumAPI.Contributors)
        {
            if (contributor.Id == 5080)
            {
                VariousArtists = new Artist(contributor.Id, contributor.Name, contributor.Role ?? "Main");
                continue;
            }

            AddContributorName(contributor.Name);
            AddContributorToRole(contributor.Role, contributor.Name, contributor.Role == "Main");
        }
    }

    private void AddContributorName(string artistName)
    {
        if (!Artists.Contains(artistName))
        {
            Artists.Add(artistName);
        }
    }

    private void AddContributorToRole(string? role, string artistName, bool isMainArtist)
    {
        if (!isMainArtist && Artist["Main"].Contains(artistName))
        {
            return;
        }

        var normalizedRole = role ?? "Main";
        if (!Artist.TryGetValue(normalizedRole, out var roleArtists))
        {
            roleArtists = new List<string>();
            Artist[normalizedRole] = roleArtists;
        }

        roleArtists.Add(artistName);
    }

    private void ApplyReleaseDate(ApiAlbum albumAPI)
    {
        var releaseDate = string.IsNullOrEmpty(albumAPI.OriginalReleaseDate)
            ? albumAPI.ReleaseDate
            : albumAPI.OriginalReleaseDate;

        if (string.IsNullOrEmpty(releaseDate))
        {
            return;
        }

        Date.Year = releaseDate.Substring(0, 4);
        Date.Month = releaseDate.Substring(5, 7);
        Date.Day = releaseDate.Substring(8, 10);
        Date.FixDayMonth();
    }

    private void UpdatePicture(ApiAlbum albumAPI)
    {
        if (Pic != null && !string.IsNullOrEmpty(Pic.Md5))
        {
            return;
        }

        Pic ??= new Picture("", "cover");
        if (!string.IsNullOrEmpty(albumAPI.Md5Image))
        {
            Pic.Md5 = albumAPI.Md5Image;
            return;
        }

        if (!string.IsNullOrEmpty(albumAPI.CoverSmall))
        {
            var coverIndex = albumAPI.CoverSmall.IndexOf("cover/", StringComparison.Ordinal);
            if (coverIndex >= 0)
            {
                Pic.Md5 = albumAPI.CoverSmall.Substring(coverIndex + 6, albumAPI.CoverSmall.Length - (coverIndex + 6) - 24);
            }
        }
    }

    private void EnsurePictureType()
    {
        if (Pic != null && string.IsNullOrEmpty(Pic.Type))
        {
            Pic.Type = "cover";
        }
    }

    private void ParseGenres(ApiAlbum albumAPI)
    {
        if (albumAPI.Genres?.Data == null || albumAPI.Genres.Data.Count == 0)
        {
            return;
        }

        foreach (var name in albumAPI.Genres.Data
                     .Select(static genre => genre.Name)
                     .Where(static name => name != null))
        {
            Genre.Add(name!);
        }
    }
}
