using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;

namespace DeezSpoTag.Integrations.Deezer
{
    public class Playlist
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string PictureMedium { get; set; } = string.Empty;
        public string CreationDate { get; set; } = string.Empty;
        public bool Public { get; set; }
        public DeezerUser Creator { get; set; } = new DeezerUser();

        // Extended properties for deezspotag compatibility
        public Picture Pic { get; set; } = new();
        public int TrackTotal { get; set; }
        public int Duration { get; set; }
        public bool IsPublic { get; set; }
        public bool IsCollaborative { get; set; }
        public CustomDate Date { get; set; } = new();
        public string DateString { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;
        public int Bitrate { get; set; }
        public string Description { get; set; } = string.Empty;

        public Playlist() { }

        public Playlist(string id, string title, string md5 = "")
        {
            Id = id;
            Title = title;
            if (!string.IsNullOrEmpty(md5))
            {
                Pic = new Picture(md5, "cover");
            }
        }
    }

}
