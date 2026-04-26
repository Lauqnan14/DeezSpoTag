using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Objects;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared.Utils;

/// <summary>
/// Path template generator for deezspotag downloads
/// Ported from: /deezspotag/deezspotag/src/utils/pathtemplates.ts
/// </summary>
public class PathTemplateGenerator
{
    private const string TrackType = "track";
    private const string EpisodeType = "episode";
    private const string PlaylistType = "playlist";
    private const string UnknownValue = "Unknown";
    private const string ArtistPlaceholder = "%artist%";
    private const string ExplicitPlaceholder = "%explicit%";
    private const string PlaylistPlaceholder = "%playlist%";
    private const string RootArtistPlaceholder = "%root_artist%";
    private const string RootArtistIdPlaceholder = "%root_artist_id%";
    private readonly DeezSpoTagSettings _settings;

    private static readonly Dictionary<int, string> BitrateLabels = new()
    {
        { 15, "360 HQ" },  // MP4_RA3
        { 14, "360 MQ" },  // MP4_RA2
        { 13, "360 LQ" },  // MP4_RA1
        { 9, "FLAC" },     // FLAC
        { 3, "320" },      // MP3_320
        { 1, "128" },      // MP3_128
        { 0, "128" },      // DEFAULT
        { -1, "MP3" }      // LOCAL
    };

    public PathTemplateGenerator(DeezSpoTagSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generate complete path information for a track
    /// Ported from: generatePath function in deezspotag pathtemplates.ts
    /// </summary>
    public static TrackPathInfo GeneratePath(Track track, string downloadObjectType, DeezSpoTagSettings settings)
    {
        var filenameTemplate = settings.TracknameTemplate;
        var singleTrack = false;

        if (downloadObjectType == TrackType || downloadObjectType == EpisodeType)
        {
            singleTrack = true;
        }

        var filename = GenerateTrackName(filenameTemplate, track, settings);
        var filepath = settings.DownloadLocation ?? ".";
        string? artistPath = null;
        string? coverPath = null;
        var extrasPath = filepath;

        // Create playlist folder if needed
        if (PathTemplateCommon.ShouldCreatePlaylistFolder(track, settings))
        {
            filepath = Path.Join(filepath, GeneratePlaylistName(track, settings));
        }

        // Create artist folder if needed
        if (PathTemplateCommon.ShouldCreateArtistFolder(track, settings))
        {
            var artistName = GenerateArtistName(
                settings.ArtistNameTemplate,
                track.MainArtist ?? new Artist { Name = UnknownValue },
                settings,
                track.Album?.RootArtist);
            filepath = Path.Join(filepath, artistName);
            artistPath = filepath;
        }

        // Create album folder if needed
        if (ShouldCreateAlbumFolder(track, settings, singleTrack))
        {
            var albumName = GenerateAlbumName(
                settings.AlbumNameTemplate,
                track.Album ?? new Album(UnknownValue),
                settings,
                track.Playlist);
            filepath = Path.Join(filepath, albumName);
            coverPath = filepath;
        }

        // Update extras path if not set for playlists
        if (track.Playlist == null || settings.Tags.SavePlaylistAsCompilation)
        {
            extrasPath = filepath;
        }

        // Create CD folder if needed
        if (ShouldCreateCDFolder(track, settings, singleTrack))
        {
            filepath = Path.Join(filepath, $"CD{track.DiskNumber}");
        }

        // Handle subfolders in filename
        if (filename.Contains('/'))
        {
            var slashIndex = filename.IndexOf('/');
            var tempPath = filename.Substring(0, slashIndex);
            filepath = Path.Join(filepath, tempPath);
            filename = filename.Substring(slashIndex + 1);
        }

        return new TrackPathInfo
        {
            Filename = filename,
            FilePath = filepath,
            ArtistPath = artistPath,
            CoverPath = coverPath,
            ExtrasPath = extrasPath
        };
    }

    /// <summary>
    /// Generate track filename from template
    /// Ported from: generateTrackName function in deezspotag pathtemplates.ts
    /// </summary>
    public static string GenerateTrackName(string filenameTemplate, Track track, DeezSpoTagSettings settings)
    {
        var c = settings.IllegalCharacterReplacer;
        var filename = filenameTemplate;

        // Basic track info
        filename = filename.Replace("%title%", FixName(track.Title ?? UnknownValue, c));
        filename = filename.Replace(ArtistPlaceholder, FixName(track.MainArtist?.Name ?? UnknownValue, c));
        var artistsValue = settings.Tags.MultiArtistSeparator == "default"
            ? string.Join(", ", track.Artists ?? new List<string>())
            : track.ArtistsString ?? "";
        filename = filename.Replace("%artists%", FixName(artistsValue, c));
        filename = filename.Replace("%tagsartists%", FixName(track.ArtistsString ?? "", c));
        filename = filename.Replace("%allartists%", FixName(track.FullArtistsString ?? "", c));
        filename = filename.Replace("%mainartists%", FixName(track.MainArtistsString ?? "", c));

        // Featured artists
        if (!string.IsNullOrEmpty(track.FeatArtistsString))
        {
            filename = filename.Replace("%featartists%", FixName($"({track.FeatArtistsString})", c));
        }
        else
        {
            filename = filename.Replace(" %featartists%", "");
            filename = filename.Replace("%featartists%", "");
        }

        // Album info
        if (track.Album != null)
        {
            filename = filename.Replace("%album%", FixName(track.Album.Title ?? UnknownValue, c));
            filename = filename.Replace("%albumartist%", FixName(track.Album.MainArtist?.Name ?? UnknownValue, c));
            filename = filename.Replace("%tracknumber%", Pad(track.TrackNumber, track.Album.TrackTotal, settings));
            filename = filename.Replace("%tracktotal%", track.Album.TrackTotal.ToString());

            filename = filename.Replace(
                "%genre%",
                track.Album.Genre?.Count > 0
                    ? FixName(track.Album.Genre[0], c)
                    : UnknownValue);

            filename = filename.Replace("%disctotal%", track.Album.DiscTotal.ToString());
            filename = filename.Replace("%label%", FixName(track.Album.Label ?? "", c));
            filename = filename.Replace("%upc%", track.Album.Barcode ?? "");
            filename = filename.Replace("%album_id%", track.Album.Id ?? "");
        }

        // Track specific info
        filename = filename.Replace("%discnumber%", track.DiskNumber.ToString());
        filename = filename.Replace("%year%", track.Date?.Year.ToString() ?? "");
        filename = filename.Replace("%date%", track.DateString ?? "");
        filename = filename.Replace("%bpm%", track.Bpm.ToString());
        filename = filename.Replace("%isrc%", track.ISRC ?? "");

        // Explicit content
        if (track.Explicit)
        {
            filename = filename.Replace(ExplicitPlaceholder, "(Explicit)");
        }
        else
        {
            filename = filename.Replace($" {ExplicitPlaceholder}", "");
            filename = filename.Replace(ExplicitPlaceholder, "");
        }

        filename = PathTemplateCommon.ApplyTrackIdTokens(filename, track);
        filename = PathTemplateCommon.ApplyPlaylistPositionTokens(
            filename,
            track,
            settings,
            clearPlaylistIdWhenAlbumMissing: false);

        // Normalize path separators
        filename = filename.Replace("\\", "/");

        return AntiDot(FixLongName(filename, settings.LimitMax));
    }

    /// <summary>
    /// Generate album folder name from template
    /// Ported from: generateAlbumName function in deezspotag pathtemplates.ts
    /// </summary>
    public static string GenerateAlbumName(string folderTemplate, Album album, DeezSpoTagSettings settings, Playlist? playlist)
    {
        var c = settings.IllegalCharacterReplacer;
        var foldername = folderTemplate;

        if (playlist != null && settings.Tags.SavePlaylistAsCompilation)
        {
            foldername = foldername.Replace("%album_id%", $"pl_{playlist.Id}");
            foldername = foldername.Replace("%genre%", "Compile");
        }
        else
        {
            foldername = foldername.Replace("%album_id%", album.Id ?? "");
            foldername = foldername.Replace(
                "%genre%",
                album.Genre?.Count > 0
                    ? FixName(album.Genre[0], c)
                    : UnknownValue);
        }

        foldername = foldername.Replace("%album%", FixName(album.Title ?? UnknownValue, c));
        foldername = foldername.Replace(ArtistPlaceholder, FixName(album.MainArtist?.Name ?? UnknownValue, c));
        foldername = foldername.Replace("%artists%", FixName(string.Join(", ", album.Artists ?? new List<string>()), c));
        foldername = foldername.Replace("%artist_id%", album.MainArtist?.Id ?? "");

        foldername = foldername.Replace(
            PlaylistPlaceholder,
            playlist != null
                ? FixName(playlist.Title ?? UnknownValue, c)
                : FixName(album.Title ?? UnknownValue, c));

        if (album.RootArtist != null)
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(album.RootArtist.Name, c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, album.RootArtist.Id);
        }
        else
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(album.MainArtist?.Name ?? UnknownValue, c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, album.MainArtist?.Id ?? "");
        }

        foldername = foldername.Replace("%tracktotal%", album.TrackTotal.ToString());
        foldername = foldername.Replace("%disctotal%", album.DiscTotal?.ToString() ?? "");
        foldername = foldername.Replace("%type%", FixName(CapitalizeFirst(album.RecordType ?? "album"), c));
        foldername = foldername.Replace("%upc%", album.Barcode ?? "");
        foldername = foldername.Replace(ExplicitPlaceholder, album.Explicit == true ? "(Explicit)" : "");
        foldername = foldername.Replace("%label%", FixName(album.Label ?? "", c));
        foldername = foldername.Replace("%year%", album.Date?.Year.ToString() ?? "");
        foldername = foldername.Replace("%date%", album.DateString ?? "");
        foldername = foldername.Replace("%bitrate%", BitrateLabels.GetValueOrDefault(album.Bitrate ?? -1, UnknownValue));

        foldername = foldername.Replace("\\", "/");
        return AntiDot(FixLongName(foldername, settings.LimitMax));
    }

    /// <summary>
    /// Generate artist folder name from template
    /// Ported from: generateArtistName function in deezspotag pathtemplates.ts
    /// </summary>
    public static string GenerateArtistName(string folderTemplate, Artist artist, DeezSpoTagSettings settings, Artist? rootArtist)
    {
        var c = settings.IllegalCharacterReplacer;
        var foldername = folderTemplate;

        foldername = foldername.Replace(ArtistPlaceholder, FixName(artist.Name, c));
        foldername = foldername.Replace("%artist_id%", artist.Id);

        if (rootArtist != null)
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(rootArtist.Name, c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, rootArtist.Id);
        }
        else
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(artist.Name, c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, artist.Id);
        }

        foldername = foldername.Replace("\\", "/");
        return AntiDot(FixLongName(foldername, settings.LimitMax));
    }

    /// <summary>
    /// Generate playlist folder name from template
    /// Ported from: generatePlaylistName function in deezspotag pathtemplates.ts
    /// </summary>
    public static string GeneratePlaylistName(Track track, DeezSpoTagSettings settings)
    {
        if (track.Playlist == null)
            return "Unknown Playlist";

        var c = settings.IllegalCharacterReplacer;
        var today = DateTime.Now;
        var todayString = today.ToString(settings.DateFormat);

        var foldername = settings.PlaylistNameTemplate;

        foldername = foldername.Replace(PlaylistPlaceholder, FixName(track.Playlist.Title ?? UnknownValue, c));
        foldername = foldername.Replace("%playlist_id%", FixName(track.Playlist.Id ?? "", c));
        foldername = foldername.Replace("%owner%", FixName(track.Playlist.Owner?.Name ?? UnknownValue, c));
        foldername = foldername.Replace("%owner_id%", track.Playlist.Owner?.Id ?? "");
        foldername = foldername.Replace("%year%", track.Playlist.Date?.Year.ToString() ?? "");
        foldername = foldername.Replace("%date%", track.Playlist.DateString ?? "");
        foldername = foldername.Replace(ExplicitPlaceholder, ""); // Playlist explicit not available in Core model
        foldername = foldername.Replace("%today%", todayString);
        foldername = foldername.Replace("\\", "/");

        return AntiDot(FixLongName(foldername, settings.LimitMax));
    }

    /// <summary>
    /// Generate download object name from template
    /// Ported from: generateDownloadObjectName function in deezspotag pathtemplates.ts
    /// </summary>
    public static string GenerateDownloadObjectName(string folderTemplate, DeezSpoTagDownloadObject queueItem, DeezSpoTagSettings settings)
    {
        var c = settings.IllegalCharacterReplacer;
        var foldername = folderTemplate;

        foldername = foldername.Replace("%title%", FixName(queueItem.Title ?? UnknownValue, c));
        foldername = foldername.Replace(ArtistPlaceholder, FixName(queueItem.Artist ?? UnknownValue, c));
        foldername = foldername.Replace("%size%", queueItem.Size.ToString());
        foldername = foldername.Replace("%type%", FixName(queueItem.Type, c));
        foldername = foldername.Replace("%id%", FixName(queueItem.Id, c));

        if (queueItem.Type == PlaylistType && queueItem is DeezSpoTagCollection collection && collection.Collection.PlaylistAPI != null)
        {
            var playlistTitle = collection.Collection.PlaylistAPI.GetValueOrDefault("title")?.ToString() ?? queueItem.Title;
            foldername = foldername.Replace(PlaylistPlaceholder, FixName(playlistTitle, c));
        }
        else
        {
            foldername = foldername.Replace(PlaylistPlaceholder, FixName(queueItem.Title ?? UnknownValue, c));
        }

        foldername = foldername.Replace("%bitrate%", BitrateLabels.GetValueOrDefault(queueItem.Bitrate, UnknownValue));
        foldername = foldername.Replace("\\", "/").Replace("/", c);

        return AntiDot(FixLongName(foldername, settings.LimitMax));
    }

    /// <summary>
    /// Generate track filename
    /// </summary>
    public string GenerateTrackFilename(Track track)
    {
        return GenerateTrackName(_settings.TracknameTemplate, track, _settings);
    }

    /// <summary>
    /// Generate track path
    /// </summary>
    public string GenerateTrackPath(Track track, string downloadType)
    {
        var pathInfo = GeneratePath(track, downloadType, _settings);
        return pathInfo.FilePath;
    }

    /// <summary>
    /// Generate album art path
    /// </summary>
    public string? GenerateAlbumArtPath(Track track)
    {
        var pathInfo = GeneratePath(track, "album", _settings);
        return pathInfo.CoverPath;
    }

    /// <summary>
    /// Generate artist art path
    /// </summary>
    public string? GenerateArtistArtPath(Track track)
    {
        var pathInfo = GeneratePath(track, "album", _settings);
        return pathInfo.ArtistPath;
    }

    #region Helper Methods

    /// <summary>
    /// Fix illegal characters in filename
    /// Ported from: fixName function in deezspotag pathtemplates.ts
    /// </summary>
    private static string FixName(string? txt, string replacement = "_")
    {
        return CjkFilenameSanitizer.SanitizeSegment(
            txt,
            fallback: string.Empty,
            replacement: replacement,
            collapseWhitespace: false,
            trimTrailingDotsAndSpaces: false);
    }

    /// <summary>
    /// Fix long names by truncating
    /// Ported from: fixLongName function in deezspotag pathtemplates.ts
    /// </summary>
    private static string FixLongName(string name, int limitMax)
    {
        return DownloadUtils.FixLongName(name, limitMax);
    }

    /// <summary>
    /// Remove trailing dots, spaces, and newlines
    /// Ported from: antiDot function in deezspotag pathtemplates.ts
    /// </summary>
    private static string AntiDot(string str)
    {
        return DownloadUtils.AntiDot(str);
    }

    /// <summary>
    /// Pad numbers with zeros
    /// Ported from: pad function in deezspotag pathtemplates.ts
    /// </summary>
    private static string Pad(int num, int maxVal, DeezSpoTagSettings settings)
    {
        return DownloadUtils.Pad(num, maxVal, settings);
    }

    /// <summary>
    /// Capitalize first letter
    /// </summary>
    private static string CapitalizeFirst(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return char.ToUpper(input[0]) + input.Substring(1).ToLower();
    }

    /// <summary>
    /// Check if album folder should be created
    /// Ported from: shouldCreateAlbumFolder function in deezspotag pathtemplates.ts
    /// </summary>
    private static bool ShouldCreateAlbumFolder(Track track, DeezSpoTagSettings settings, bool singleTrack)
    {
        return settings.CreateAlbumFolder &&
               (!singleTrack || settings.CreateSingleFolder) &&
               (track.Playlist == null ||
                settings.Tags.SavePlaylistAsCompilation ||
                settings.CreateStructurePlaylist);
    }

    /// <summary>
    /// Check if CD folder should be created
    /// Ported from: shouldCreateCDFolder function in deezspotag pathtemplates.ts
    /// </summary>
    private static bool ShouldCreateCDFolder(Track track, DeezSpoTagSettings settings, bool singleTrack)
    {
        return track.Album?.DiscTotal > 1 &&
               settings.CreateAlbumFolder &&
               settings.CreateCDFolder &&
               (!singleTrack || settings.CreateSingleFolder) &&
               (track.Playlist == null ||
                settings.Tags.SavePlaylistAsCompilation ||
                settings.CreateStructurePlaylist);
    }

    #endregion
}

/// <summary>
/// Track path information
/// </summary>
public class TrackPathInfo
{
    public string Filename { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? ArtistPath { get; set; }
    public string? CoverPath { get; set; }
    public string ExtrasPath { get; set; } = "";
}
