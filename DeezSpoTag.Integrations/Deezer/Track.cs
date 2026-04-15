using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Integrations.Deezer
{
    public class Track
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Duration { get; set; }
        public int TrackPosition { get; set; }
        public int TrackNumber { get; set; }
        public int DiskNumber { get; set; }
        public int DiscNumber { get; set; }
        public bool ExplicitLyrics { get; set; }
        public bool Explicit { get; set; }
        public string Isrc { get; set; } = string.Empty;
        public string ISRC { get; set; } = string.Empty;
        public Artist? Artist { get; set; }
        public Album? Album { get; set; }
        public Artist? MainArtist { get; set; }

        // Extended properties for deezspotag compatibility
        public string TrackToken { get; set; } = string.Empty;
        public int TrackTokenExpire { get; set; }
        public int? TrackTokenExpiration { get; set; }
        public string MD5 { get; set; } = string.Empty;
        public string MediaVersion { get; set; } = string.Empty;
        public Dictionary<string, int> FileSizes { get; set; } = new();
        public Dictionary<string, string> Urls { get; set; } = new();
        public string ReplayGain { get; set; } = string.Empty;
        public string LyricsId { get; set; } = string.Empty;
        public string Copyright { get; set; } = string.Empty;
        public string PhysicalReleaseDate { get; set; } = string.Empty;
        public int FallbackID { get; set; }
        public int FallbackId { get; set; }
        public List<string> AlbumsFallback { get; set; } = new();
        public bool Searched { get; set; }
        public bool IsLocal { get; set; }
        public bool Local { get; set; }
        public int Bitrate { get; set; }
        public double Bpm { get; set; }
        public double Gain { get; set; }
        public int Rank { get; set; }
        public Dictionary<string, object> Contributors { get; set; } = new();
        public Lyrics? Lyrics { get; set; }
        public Playlist? Playlist { get; set; }

        // Extension method for parsing essential data
        public static void ParseEssentialData()
        {
            // This method can be used for compatibility
        }
    }
}
