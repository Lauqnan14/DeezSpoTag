namespace DeezSpoTag.Core.Models;

/// <summary>
/// Playlist model (ported from deezspotag Playlist.ts)
/// </summary>
public class Playlist
{
    public string Id { get; set; } = "0";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Picture Pic { get; set; } = new();
    public int TrackTotal { get; set; }
    public int Duration { get; set; }
    public CustomDate Date { get; set; } = new();
    public string DateString { get; set; } = "";
    public Artist? Owner { get; set; }
    public bool IsPublic { get; set; } = true;
    public bool IsCollaborative { get; set; } = false;
    public string Checksum { get; set; } = "";
    public int Bitrate { get; set; }

    // Additional properties for compatibility
    public List<Track> Tracks { get; set; } = new();
    public Artist? Creator => Owner;
    public DateTime? CreationDate { get; set; }

    public Playlist()
    {
    }

    public Playlist(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public Playlist(Dictionary<string, object> playlistAPI)
    {
        ParsePlaylist(playlistAPI);
    }

    /// <summary>
    /// Parse playlist data from API response (ported from deezspotag parsePlaylist)
    /// </summary>
    public void ParsePlaylist(Dictionary<string, object> playlistAPI)
    {
        try
        {
            ParseBasicDetails(playlistAPI);
            ParsePicture(playlistAPI);
            ParseCounts(playlistAPI);
            ParseCreationDate(playlistAPI);
            Owner = TryParseOwner(playlistAPI);
            ParseFlags(playlistAPI);
            ParseChecksum(playlistAPI);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If parsing fails, keep default values
        }
    }

    /// <summary>
    /// Get playlist picture URL for specified size
    /// </summary>
    public string GetPictureUrl(int size = 500)
    {
        return Pic.GetURL(size);
    }

    private void ParseBasicDetails(Dictionary<string, object> playlistAPI)
    {
        if (playlistAPI.TryGetValue("id", out var id))
        {
            Id = id.ToString() ?? "0";
        }

        if (playlistAPI.TryGetValue("title", out var title))
        {
            Title = title.ToString() ?? "";
        }

        if (playlistAPI.TryGetValue("description", out var description))
        {
            Description = description.ToString() ?? "";
        }
    }

    private void ParsePicture(Dictionary<string, object> playlistAPI)
    {
        if (!playlistAPI.TryGetValue("picture_small", out var picture))
        {
            return;
        }

        var pictureMd5 = ExtractPictureMd5(picture.ToString());
        if (!string.IsNullOrEmpty(pictureMd5))
        {
            Pic = new Picture(pictureMd5, "playlist");
        }
    }

    private void ParseCounts(Dictionary<string, object> playlistAPI)
    {
        if (playlistAPI.TryGetValue("nb_tracks", out var trackTotal))
        {
            TrackTotal = Convert.ToInt32(trackTotal);
        }

        if (playlistAPI.TryGetValue("duration", out var duration))
        {
            Duration = Convert.ToInt32(duration);
        }
    }

    private void ParseCreationDate(Dictionary<string, object> playlistAPI)
    {
        if (playlistAPI.TryGetValue("creation_date", out var creationDate))
        {
            Date = CustomDate.FromString(creationDate.ToString() ?? "");
        }
    }

    private static Artist? TryParseOwner(Dictionary<string, object> playlistAPI)
    {
        if (TryGetOwner(playlistAPI, "creator", out var creator))
        {
            return creator;
        }

        return TryGetOwner(playlistAPI, "user", out var user) ? user : null;
    }

    private static bool TryGetOwner(Dictionary<string, object> playlistAPI, string key, out Artist? owner)
    {
        owner = null;
        if (!playlistAPI.TryGetValue(key, out var ownerObject) || ownerObject is not Dictionary<string, object> ownerDictionary)
        {
            return false;
        }

        var ownerId = ownerDictionary.GetValueOrDefault("id")?.ToString() ?? "0";
        var ownerName = ownerDictionary.GetValueOrDefault("name")?.ToString() ?? "Unknown";
        owner = new Artist(ownerId, ownerName, "Owner");
        return true;
    }

    private void ParseFlags(Dictionary<string, object> playlistAPI)
    {
        if (playlistAPI.TryGetValue("public", out var isPublic))
        {
            IsPublic = Convert.ToBoolean(isPublic);
        }

        if (playlistAPI.TryGetValue("collaborative", out var isCollaborative))
        {
            IsCollaborative = Convert.ToBoolean(isCollaborative);
        }
    }

    private void ParseChecksum(Dictionary<string, object> playlistAPI)
    {
        if (playlistAPI.TryGetValue("checksum", out var checksum))
        {
            Checksum = checksum.ToString() ?? "";
        }
    }

    /// <summary>
    /// Get playlist link
    /// </summary>
    public string GetLink()
    {
        return $"https://www.deezer.com/playlist/{Id}";
    }

    /// <summary>
    /// Get formatted duration string
    /// </summary>
    public string GetFormattedDuration()
    {
        var hours = Duration / 3600;
        var minutes = (Duration % 3600) / 60;
        var seconds = Duration % 60;

        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}"
            : $"{minutes}:{seconds:D2}";
    }

    /// <summary>
    /// Extract picture MD5 from Deezer picture URL
    /// </summary>
    private static string? ExtractPictureMd5(string? pictureUrl)
    {
        if (string.IsNullOrEmpty(pictureUrl)) return null;

        try
        {
            // Extract MD5 from URL like: https://e-cdns-images.dzcdn.net/images/playlist/f2bc007e9133c946ac3c3907ddc5d2ea/56x56-000000-80-0-0.jpg
            var parts = pictureUrl.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if ((parts[i] == "playlist" || parts[i] == "cover") && i + 1 < parts.Length)
                {
                    return parts[i + 1];
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If extraction fails, return null
        }

        return null;
    }

    public override string ToString()
    {
        return Title;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != typeof(Playlist))
        {
            return false;
        }

        var other = (Playlist)obj;
        return Id == other.Id && Title == other.Title;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Title);
    }
}
