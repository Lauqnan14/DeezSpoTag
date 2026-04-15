using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Integrations.Deezer
{
    public class Album
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CoverMedium { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;
        public string RecordType { get; set; } = "album";
        public Artist? Artist { get; set; }

        // Extended properties for deezspotag compatibility
        public Picture Pic { get; set; } = new();
        public Artist? MainArtist { get; set; }
        public Artist? RootArtist { get; set; }
        public int TrackTotal { get; set; }
        public int DiscTotal { get; set; }
        public bool Explicit { get; set; }
        public string PhysicalReleaseDate { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Copyright { get; set; } = string.Empty;
        public List<string> Genre { get; set; } = new();
        public CustomDate Date { get; set; } = new();
        public string DateString { get; set; } = string.Empty;
        public int Bitrate { get; set; }
        public string EmbeddedCoverURL { get; set; } = string.Empty;
        public string EmbeddedCoverPath { get; set; } = string.Empty;
        public bool IsPlaylist { get; set; }
        public List<Track> Tracks { get; set; } = new();
        public Dictionary<string, List<string>> Artists { get; set; } = new();

        public Album() { }

        public Album(string id, string title, string md5 = "")
        {
            Id = id;
            Title = title;
            if (!string.IsNullOrEmpty(md5))
            {
                Pic = new Picture(md5, "cover");
            }
        }
    }

    public class Artist
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Picture Pic { get; set; } = new();
        public string Role { get; set; } = string.Empty;

        public Artist() { }

        public Artist(string id, string name, string role = "")
        {
            Id = id;
            Name = name;
            Role = role;
        }
    }
}
