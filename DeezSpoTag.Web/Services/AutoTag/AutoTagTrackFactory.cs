namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagTrackFactory
{
    internal sealed class DanceTrackInput
    {
        public required string Title { get; init; }
        public string? Version { get; init; }
        public required IEnumerable<string> Artists { get; init; }
        public required IEnumerable<string> AlbumArtists { get; init; }
        public string? Album { get; init; }
        public string? Key { get; init; }
        public long? Bpm { get; init; }
        public required IEnumerable<string> Genres { get; init; }
        public string? Art { get; init; }
        public string? Url { get; init; }
        public string? Label { get; init; }
        public string? CatalogNumber { get; init; }
        public string? ReleaseId { get; init; }
        public TimeSpan? Duration { get; init; }
        public int? TrackNumber { get; init; }
        public int? TrackTotal { get; init; }
        public DateTime? ReleaseDate { get; init; }
    }

    public static AutoTagTrack CreateDanceTrack(DanceTrackInput input)
    {
        return new AutoTagTrack
        {
            Title = input.Title,
            Version = input.Version,
            Artists = input.Artists.ToList(),
            AlbumArtists = input.AlbumArtists.ToList(),
            Album = input.Album,
            Key = input.Key,
            Bpm = input.Bpm,
            Genres = input.Genres.ToList(),
            Art = input.Art,
            Url = input.Url,
            Label = input.Label,
            CatalogNumber = input.CatalogNumber,
            ReleaseId = input.ReleaseId,
            Duration = input.Duration,
            TrackNumber = input.TrackNumber,
            TrackTotal = input.TrackTotal,
            ReleaseDate = input.ReleaseDate
        };
    }

    public static AutoTagTrack FromTraxsource(TraxsourceTrackInfo track)
        => CreateDanceTrack(BuildDanceTrackInput(track));

    public static AutoTagTrack FromBeatport(BeatportTrackInfo track)
    {
        var autoTagTrack = CreateDanceTrack(BuildDanceTrackInput(ToTraxsourceTrackInfo(track)));

        autoTagTrack.Styles = track.Styles.ToList();
        autoTagTrack.TrackId = track.TrackId;
        autoTagTrack.Remixers = track.Remixers.ToList();
        autoTagTrack.Isrc = track.Isrc;
        autoTagTrack.PublishDate = track.PublishDate;
        autoTagTrack.Other = track.Other.ToDictionary(k => k.Key, v => v.Values);
        return autoTagTrack;
    }

    private static DanceTrackInput BuildDanceTrackInput(TraxsourceTrackInfo track)
    {
        return new DanceTrackInput
        {
            Title = track.Title,
            Version = track.Version,
            Artists = track.Artists,
            AlbumArtists = track.AlbumArtists,
            Album = track.Album,
            Key = track.Key,
            Bpm = track.Bpm,
            Genres = track.Genres,
            Art = track.Art,
            Url = track.Url,
            Label = track.Label,
            CatalogNumber = track.CatalogNumber,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            TrackNumber = track.TrackNumber,
            TrackTotal = track.TrackTotal,
            ReleaseDate = track.ReleaseDate
        };
    }

    private static TraxsourceTrackInfo ToTraxsourceTrackInfo(BeatportTrackInfo track)
    {
        var mapped = new TraxsourceTrackInfo();
        mapped.Title = track.Title;
        mapped.Version = track.Version;
        mapped.Artists = track.Artists.ToList();
        mapped.AlbumArtists = track.AlbumArtists.ToList();
        mapped.Album = track.Album;
        mapped.Key = track.Key;
        mapped.Bpm = track.Bpm;
        mapped.Genres = track.Genres.ToList();
        mapped.Art = track.Art;
        mapped.Url = track.Url;
        mapped.Label = track.Label;
        mapped.CatalogNumber = track.CatalogNumber;
        mapped.ReleaseId = track.ReleaseId;
        mapped.Duration = track.Duration;
        mapped.TrackNumber = track.TrackNumber;
        mapped.TrackTotal = track.TrackTotal;
        mapped.ReleaseDate = track.ReleaseDate;
        return mapped;
    }

    public static AutoTagTrack FromBeatsource(BeatsourceTrackInfo track)
    {
        var autoTagTrack = CreateDanceTrack(new DanceTrackInput
        {
            Title = track.Title,
            Version = track.Version,
            Artists = track.Artists,
            AlbumArtists = Array.Empty<string>(),
            Album = track.Album,
            Key = track.Key,
            Bpm = track.Bpm,
            Genres = track.Genres,
            Art = track.Art,
            Url = track.Url,
            Label = track.Label,
            CatalogNumber = track.CatalogNumber,
            ReleaseId = track.ReleaseId,
            Duration = track.Duration,
            TrackNumber = null,
            TrackTotal = null,
            ReleaseDate = track.ReleaseDate
        });

        autoTagTrack.TrackId = track.TrackId;
        autoTagTrack.Remixers = track.Remixers.ToList();
        autoTagTrack.Isrc = track.Isrc;
        return autoTagTrack;
    }
}
