namespace DeezSpoTag.Core.Models;

/// <summary>
/// Artist model (ported from deezspotag Artist.ts)
/// </summary>
public class Artist
{
    public string Id { get; set; } = "0";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "Main";
    public Picture Pic { get; set; } = new();
    public bool Save { get; set; } = true;
    public string? QobuzId { get; set; }

    public Artist()
    {
    }

    public Artist(string name)
    {
        Id = "0";
        Name = name;
        Role = "Main";
    }

    public Artist(long id, string name, string role = "Main", string? pictureMd5 = null)
    {
        Id = id.ToString();
        Name = name;
        Role = role;
        if (!string.IsNullOrEmpty(pictureMd5))
        {
            Pic = new Picture(pictureMd5, "artist");
        }
    }

    public Artist(string id, string name, string role = "Main", string? pictureMd5 = null)
    {
        Id = id;
        Name = name;
        Role = role;
        if (!string.IsNullOrEmpty(pictureMd5))
        {
            Pic = new Picture(pictureMd5, "artist");
        }
    }

    /// <summary>
    /// Check if this is the "Various Artists" artist
    /// </summary>
    public bool IsVariousArtists()
    {
        return Name.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) ||
               Id == "5080"; // Deezer's Various Artists ID
    }

    /// <summary>
    /// Get artist picture URL for specified size
    /// </summary>
    public string GetPictureUrl(int size = 500)
    {
        if (string.IsNullOrEmpty(Pic.Md5))
            return "";

        return Pic.GetURL(size);
    }

    /// <summary>
    /// Get artist link
    /// </summary>
    public string GetLink()
    {
        return $"https://www.deezer.com/artist/{Id}";
    }

    public override string ToString()
    {
        return Name;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != typeof(Artist))
        {
            return false;
        }

        var other = (Artist)obj;
        return Id == other.Id && Name == other.Name;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Name);
    }
}
