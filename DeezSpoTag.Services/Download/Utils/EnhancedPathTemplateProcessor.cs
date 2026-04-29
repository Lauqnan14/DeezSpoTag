using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Core.Enums;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Consolidated path processing utilities merging EnhancedPathTemplateProcessor and DownloadUtils
/// EXACT PORT from deezspotag pathtemplates.ts and downloadUtils.ts
/// </summary>
public class EnhancedPathTemplateProcessor
{
    private const string UnknownValue = "Unknown";
    private const string UnknownArtist = "Unknown Artist";
    private const string ArtistPlaceholder = "%artist%";
    private const string ExplicitPlaceholder = "%explicit%";
    private const string PlaylistPlaceholder = "%playlist%";
    private const string RootArtistPlaceholder = "%root_artist%";
    private const string RootArtistIdPlaceholder = "%root_artist_id%";
    private readonly ILogger<EnhancedPathTemplateProcessor> _logger;

    // EXACT PORT: Bitrate labels from deezspotag pathtemplates.ts
    private static readonly Dictionary<int, string> BitrateLabels = new()
    {
        { 13, "360 HQ" },   // MP4_RA3
        { 14, "360 MQ" },   // MP4_RA2
        { 15, "360 LQ" },   // MP4_RA1
        { 9, "FLAC" },      // FLAC
        { 3, "320" },       // MP3_320
        { 1, "128" },       // MP3_128
        { 8, "128" },       // DEFAULT
        { 0, "MP3" }        // LOCAL
    };

    public EnhancedPathTemplateProcessor(ILogger<EnhancedPathTemplateProcessor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// EXACT PORT: generatePath function from deezspotag pathtemplates.ts
    /// </summary>
    public PathGenerationResult GeneratePaths(Track track, string downloadObjectType, DeezSpoTagSettings settings)
    {
        // EXACT PORT: Determine filename template based on download type
        var filenameTemplate = ResolveFilenameTemplate(settings);

        // EXACT PORT: Generate filename from template
        var filename = GenerateTrackName(filenameTemplate, track, settings);

        // EXACT PORT: Build filepath step by step
        var filepath = settings.DownloadLocation ?? ".";
        string? artistPath = null;
        string? coverPath = null;
        string? extrasPath = null;

        // EXACT PORT: Create playlist folder if needed
        if (PathTemplateCommon.ShouldCreatePlaylistFolder(track, settings))
        {
            filepath += $"/{GeneratePlaylistName(track, settings)}";
        }

        // EXACT PORT: Set extrasPath for playlists
        if (track.Playlist != null && !settings.Tags.SavePlaylistAsCompilation)
        {
            extrasPath = filepath;
        }

        // EXACT PORT: Create artist folder if needed
        if (PathTemplateCommon.ShouldCreateArtistFolder(track, settings))
        {
            var artistToUse = ResolveArtistForFolder(track);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Creating artist folder - Track: {TrackTitle}, Album Artist: {AlbumArtist}, Track Artist: {TrackArtist}, Using: {UsingArtist}",
                    track.Title,
                    track.Album?.MainArtist?.Name ?? "NULL",
                    track.MainArtist?.Name ?? "NULL",
                    artistToUse?.Name ?? "NULL");            }

            var artistName = GenerateArtistName(
                settings.ArtistNameTemplate,
                artistToUse,
                settings,
                track.Album?.RootArtist);
            filepath += $"/{artistName}";
            artistPath = filepath;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Artist folder created: {ArtistFolder}", artistName);            }
        }

        // EXACT PORT: Create album folder if needed
        if (ShouldCreateAlbumFolder(track, settings))
        {
            var albumName = GenerateAlbumName(
                settings.AlbumNameTemplate,
                track.Album,
                settings,
                track.Playlist);
            filepath += $"/{albumName}";
            coverPath = filepath;
        }

        // EXACT PORT: Set extrasPath if not set yet
        if (string.IsNullOrEmpty(extrasPath))
        {
            extrasPath = filepath;
        }

        // EXACT PORT: Create CD folder if needed
        if (ShouldCreateCDFolder(track, settings))
        {
            filepath += $"/CD{track.DiscNumber}";
        }

        // EXACT PORT: Handle subfolders in filename
        (filepath, filename) = ApplyFilenameSubfolder(filepath, filename);

        return new PathGenerationResult
        {
            Filename = filename,
            FilePath = filepath,
            ArtistPath = artistPath,
            CoverPath = coverPath,
            ExtrasPath = extrasPath ?? filepath,
            WritePath = Path.Join(filepath, filename)
        };
    }

    /// <summary>
    /// EXACT PORT: generateTrackName function from deezspotag pathtemplates.ts
    /// </summary>
    public string GenerateTrackName(string template, Track track, DeezSpoTagSettings settings)
    {
        var c = settings.IllegalCharacterReplacer;
        var filename = template;
        var mainArtistName = GetPathArtistName(track.MainArtist?.Name, settings);
        var normalizedMainArtist = FixName(mainArtistName, c);

        // EXACT PORT: Basic track information
        filename = filename.Replace("%title%", FixName(track.Title, c));
        filename = filename.Replace(ArtistPlaceholder, normalizedMainArtist);
        var artistsValue = settings.Tags.MultiArtistSeparator == "default"
            ? string.Join(", ", track.Artists)
            : track.ArtistsString;
        filename = filename.Replace("%artists%", FixName(artistsValue, c));
        filename = filename.Replace("%tagsartists%", FixName(track.ArtistsString, c));
        filename = filename.Replace("%allartists%", FixName(track.FullArtistsString, c));
        filename = filename.Replace("%mainartists%", FixName(track.MainArtistsString, c));

        // EXACT PORT: Featured artists handling
        var featArtistsValue = !string.IsNullOrEmpty(track.FeatArtistsString)
            ? FixName($"({track.FeatArtistsString})", c)
            : string.Empty;
        filename = string.IsNullOrEmpty(featArtistsValue)
            ? filename.Replace(" %featartists%", "").Replace("%featartists%", "")
            : filename.Replace("%featartists%", featArtistsValue);

        // EXACT PORT: Album information
        if (track.Album != null)
        {
            var albumMainArtistName = GetPathArtistName(track.Album.MainArtist?.Name, settings);
            filename = filename.Replace("%album%", FixName(track.Album.Title, c));
            filename = filename.Replace("%albumartist%", FixName(albumMainArtistName, c));
            filename = filename.Replace("%tracknumber%", Pad(track.TrackNumber, track.Album.TrackTotal, settings));
            filename = filename.Replace("%tracktotal%", track.Album.TrackTotal.ToString());

            var trackGenre = track.Album.Genre is { Count: > 0 }
                ? FixName(track.Album.Genre[0], c)
                : UnknownValue;
            filename = filename.Replace("%genre%", trackGenre);

            filename = filename.Replace("%disctotal%", (track.Album.DiscTotal ?? 1).ToString());
            filename = filename.Replace("%label%", FixName(track.Album.Label ?? "", c));
            filename = filename.Replace("%upc%", track.Album.Barcode ?? "");
            filename = filename.Replace("%album_id%", track.Album.Id ?? "");
        }

        // EXACT PORT: Track metadata
        filename = filename.Replace("%discnumber%", track.DiscNumber.ToString());
        filename = filename.Replace("%year%", track.Date?.Year ?? "");
        filename = filename.Replace("%date%", track.DateString ?? "");
        filename = filename.Replace("%bpm%", track.BPM.ToString());
        filename = filename.Replace("%isrc%", track.ISRC ?? "");

        // EXACT PORT: Explicit content handling
        var explicitValue = track.Explicit ? "(Explicit)" : string.Empty;
        filename = string.IsNullOrEmpty(explicitValue)
            ? filename.Replace(" %explicit%", "").Replace(ExplicitPlaceholder, "")
            : filename.Replace(ExplicitPlaceholder, explicitValue);

        filename = PathTemplateCommon.ApplyTrackIdTokens(filename, track);
        filename = PathTemplateCommon.ApplyPlaylistPositionTokens(
            filename,
            track,
            settings,
            clearPlaylistIdWhenAlbumMissing: true);

        // EXACT PORT: Normalize path separators and clean up
        filename = filename.Replace("\\", "/");
        filename = PathTemplateCommon.CollapseRepeatedLeadingArtistPrefix(filename, template, normalizedMainArtist);
        return AntiDot(FixLongName(filename, settings.LimitMax));
    }

    /// <summary>
    /// EXACT PORT: generateAlbumName function from deezspotag pathtemplates.ts
    /// </summary>
    public string GenerateAlbumName(string template, Album? album, DeezSpoTagSettings settings, Playlist? playlist)
    {
        if (album == null) return "Unknown Album";

        var c = settings.IllegalCharacterReplacer;
        var foldername = template;

        // EXACT PORT: Playlist as compilation handling
        if (playlist != null && settings.Tags.SavePlaylistAsCompilation)
        {
            foldername = foldername.Replace("%album_id%", $"pl_{playlist.Id}");
            foldername = foldername.Replace("%genre%", "Compile");
        }
        else
        {
            foldername = foldername.Replace("%album_id%", album.Id ?? "");
            var albumGenre = album.Genre is { Count: > 0 }
                ? FixName(album.Genre[0], c)
                : UnknownValue;
            foldername = foldername.Replace("%genre%", albumGenre);
        }

        // EXACT PORT: Album information
        var albumMainArtistName = GetPathArtistName(album.MainArtist?.Name, settings);
        foldername = foldername.Replace("%album%", FixName(album.Title, c));
        foldername = foldername.Replace(ArtistPlaceholder, FixName(albumMainArtistName, c));
        foldername = foldername.Replace("%artists%", FixName(string.Join(", ", album.Artists ?? new List<string>()), c));
        foldername = foldername.Replace("%artist_id%", album.MainArtist?.Id ?? "");

        // EXACT PORT: Playlist handling
        var playlistName = playlist != null ? playlist.Title : album.Title;
        foldername = foldername.Replace(PlaylistPlaceholder, FixName(playlistName, c));

        // EXACT PORT: Root artist handling
        if (album.RootArtist != null)
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(GetPathArtistName(album.RootArtist.Name, settings), c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, album.RootArtist.Id ?? "");
        }
        else
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(albumMainArtistName, c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, album.MainArtist?.Id ?? "");
        }

        // EXACT PORT: Album metadata
        foldername = foldername.Replace("%tracktotal%", album.TrackTotal.ToString());
        foldername = foldername.Replace("%disctotal%", (album.DiscTotal ?? 1).ToString());
        foldername = foldername.Replace("%type%", FixName(CapitalizeFirst(album.RecordType ?? "album"), c));
        foldername = foldername.Replace("%upc%", album.Barcode ?? "");
        foldername = foldername.Replace(ExplicitPlaceholder, album.Explicit == true ? "(Explicit)" : "");
        foldername = foldername.Replace("%label%", FixName(album.Label ?? "", c));
        foldername = foldername.Replace("%year%", album.Date?.Year ?? "");
        foldername = foldername.Replace("%date%", album.DateString ?? "");
        foldername = foldername.Replace("%bitrate%", BitrateLabels.TryGetValue(album.Bitrate ?? 0, out var bitrateLabel) ? bitrateLabel : UnknownValue);

        // EXACT PORT: Normalize and clean up
        foldername = foldername.Replace("\\", "/");
        return AntiDot(FixLongName(foldername, settings.LimitMax));
    }

    /// <summary>
    /// EXACT PORT: generateArtistName function from deezspotag pathtemplates.ts
    /// CRITICAL FIX: Add logging and better null handling to prevent UnknownArtist folders
    /// </summary>
    public string GenerateArtistName(string template, Artist? artist, DeezSpoTagSettings settings, Artist? rootArtist)
    {
        var normalizedArtistName = GetPathArtistName(artist?.Name, settings);
        // CRITICAL FIX: Log what we're working with to debug UnknownArtist issue
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("GenerateArtistName called with artist: {ArtistName} (normalized: {NormalizedArtistName}) (ID: {ArtistId}), template: {Template}",
                artist?.Name ?? "NULL", normalizedArtistName, artist?.Id ?? "NULL", template);        }

        if (artist == null)
        {
            _logger.LogWarning("Artist is null in GenerateArtistName, returning 'Unknown Artist'");
            return UnknownArtist;
        }

        if (string.IsNullOrEmpty(normalizedArtistName) || normalizedArtistName == UnknownValue || normalizedArtistName == UnknownArtist)
        {
            _logger.LogWarning("Artist name is empty or 'Unknown' in GenerateArtistName: '{ArtistName}', returning 'Unknown Artist'", normalizedArtistName);
            return UnknownArtist;
        }

        var c = settings.IllegalCharacterReplacer;
        var foldername = template;

        foldername = foldername.Replace(ArtistPlaceholder, FixName(normalizedArtistName, c));
        foldername = foldername.Replace("%artist_id%", artist.Id ?? "");

        if (rootArtist != null)
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(GetPathArtistName(rootArtist.Name, settings), c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, rootArtist.Id ?? "");
        }
        else
        {
            foldername = foldername.Replace(RootArtistPlaceholder, FixName(normalizedArtistName, c));
            foldername = foldername.Replace(RootArtistIdPlaceholder, artist.Id ?? "");
        }

        foldername = foldername.Replace("\\", "/");
        var result = AntiDot(FixLongName(foldername, settings.LimitMax));

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("GenerateArtistName result: {Result}", result);        }
        return result;
    }

    /// <summary>
    /// EXACT PORT: generatePlaylistName function from deezspotag pathtemplates.ts
    /// </summary>
    public string GeneratePlaylistName(Track track, DeezSpoTagSettings settings)
    {
        if (track.Playlist == null) return "Unknown Playlist";

        var c = settings.IllegalCharacterReplacer;
        var today = DateTime.Now;
        var todayFormatted = FormatDate(today, settings.DateFormat);

        var foldername = settings.PlaylistNameTemplate;

        foldername = foldername.Replace(PlaylistPlaceholder, FixName(track.Playlist.Title, c));
        foldername = foldername.Replace("%playlist_id%", FixName(track.Playlist.Id ?? "", c));
        foldername = foldername.Replace("%owner%", FixName(track.Playlist.Owner?.Name ?? UnknownValue, c));
        foldername = foldername.Replace("%owner_id%", track.Playlist.Owner?.Id ?? "");
        foldername = foldername.Replace("%year%", track.Playlist.Date?.Year ?? "");
        foldername = foldername.Replace("%date%", track.Playlist.DateString ?? "");
        foldername = foldername.Replace(ExplicitPlaceholder, "");
        foldername = foldername.Replace("%today%", todayFormatted);
        foldername = foldername.Replace("\\", "/");

        return AntiDot(FixLongName(foldername, settings.LimitMax));
    }

    /// <summary>
    /// Generate download object name from template
    /// Ported from: generateDownloadObjectName function in deezspotag pathtemplates.ts
    /// </summary>
    public string GenerateDownloadObjectName(string folderTemplate, Core.Models.Download.DownloadObject downloadObject, DeezSpoTagSettings settings)
    {
        var c = settings.IllegalCharacterReplacer;
        var foldername = folderTemplate;

        foldername = foldername.Replace("%title%", FixName(downloadObject.Title ?? UnknownValue, c));
        foldername = foldername.Replace("%size%", downloadObject.Size.ToString());
        foldername = foldername.Replace("%type%", FixName(downloadObject.Type, c));
        foldername = foldername.Replace("%bitrate%", BitrateLabels.TryGetValue(downloadObject.Bitrate, out var bitrateLabel) ? bitrateLabel : UnknownValue);

        if (downloadObject is CollectionDownloadObject collection)
        {
            if (collection.Album != null)
            {
                foldername = foldername.Replace(ArtistPlaceholder, FixName(GetPathArtistName(collection.Album.MainArtist?.Name, settings), c));
                foldername = foldername.Replace("%id%", FixName(collection.Album.Id ?? "", c));
            }
            else if (collection.Playlist != null)
            {
                foldername = foldername.Replace(ArtistPlaceholder, FixName(collection.Playlist.Owner?.Name ?? UnknownValue, c));
                foldername = foldername.Replace("%id%", FixName(collection.Playlist.Id ?? "", c));
                foldername = foldername.Replace(PlaylistPlaceholder, FixName(collection.Playlist.Title ?? UnknownValue, c));
            }
            else
            {
                foldername = foldername.Replace(ArtistPlaceholder, UnknownValue);
                foldername = foldername.Replace("%id%", "");
            }
        }
        else if (downloadObject is SingleDownloadObject single)
        {
            foldername = foldername.Replace(ArtistPlaceholder, FixName(GetPathArtistName(single.Track?.MainArtist?.Name, settings), c));
            foldername = foldername.Replace("%id%", FixName(single.Track?.Id ?? "", c));
        }
        else
        {
            foldername = foldername.Replace(ArtistPlaceholder, UnknownValue);
            foldername = foldername.Replace("%id%", "");
        }

        foldername = foldername.Replace(PlaylistPlaceholder, FixName(downloadObject.Title ?? UnknownValue, c));
        foldername = foldername.Replace("\\", "/").Replace("/", c);

        return AntiDot(FixLongName(foldername, settings.LimitMax));
    }

    // EXACT PORT: Helper functions from deezspotag pathtemplates.ts

    /// <summary>
    /// EXACT PORT: fixName function from deezspotag pathtemplates.ts
    /// </summary>
    private static string FixName(string txt, string charReplacement = "_")
    {
        return CjkFilenameSanitizer.SanitizeSegment(
            txt,
            fallback: string.Empty,
            replacement: charReplacement,
            collapseWhitespace: false,
            trimTrailingDotsAndSpaces: false);
    }

    private static string GetPathArtistName(string? artistName, DeezSpoTagSettings settings)
    {
        var fallback = string.IsNullOrWhiteSpace(artistName) ? UnknownValue : artistName.Trim();
        if (settings.Tags?.SingleAlbumArtist != true)
        {
            return fallback;
        }

        var (primary, _) = ArtistNameNormalizer.SplitCombinedName(fallback);
        return string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();
    }

    /// <summary>
    /// EXACT PORT: fixLongName function from deezspotag pathtemplates.ts
    /// </summary>
    private static string FixLongName(string name, int limitMax)
    {
        return DownloadUtils.FixLongName(name, limitMax);
    }

    /// <summary>
    /// EXACT PORT: antiDot function from deezspotag pathtemplates.ts
    /// </summary>
    private string AntiDot(string str)
    {
        if (string.IsNullOrEmpty(str))
        {
            _logger.LogWarning("AntiDot received empty string, returning 'Unknown'");
            return UnknownValue;
        }

        var normalized = DownloadUtils.AntiDot(str);
        if (string.Equals(normalized, "dot", StringComparison.Ordinal))
        {
            _logger.LogWarning("AntiDot resulted in empty string after cleanup, returning 'Unknown'");
            return UnknownValue;
        }

        return normalized;
    }

    /// <summary>
    /// EXACT PORT: pad function from deezspotag pathtemplates.ts
    /// </summary>
    private static string Pad(int num, int maxVal, DeezSpoTagSettings settings)
    {
        return DownloadUtils.Pad(num, maxVal, settings);
    }

    /// <summary>
    /// Format date according to deezspotag format string
    /// </summary>
    private static string FormatDate(DateTime date, string format)
    {
        return format
            .Replace("Y", date.Year.ToString())
            .Replace("M", date.Month.ToString("D2"))
            .Replace("D", date.Day.ToString("D2"));
    }

    /// <summary>
    /// Capitalize first letter
    /// </summary>
    private static string CapitalizeFirst(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return char.ToUpper(input[0]) + input.Substring(1);
    }

    // EXACT PORT: Folder creation logic from deezspotag pathtemplates.ts

    private static bool ShouldCreateAlbumFolder(Track track, DeezSpoTagSettings settings)
    {
        return settings.CreateAlbumFolder &&
               (track.Playlist == null ||
                settings.Tags.SavePlaylistAsCompilation ||
                settings.CreateStructurePlaylist);
    }

    private static bool ShouldCreateCDFolder(Track track, DeezSpoTagSettings settings)
    {
        return track.Album?.DiscTotal > 1 &&
               settings.CreateAlbumFolder &&
               settings.CreateCDFolder &&
               (track.Playlist == null ||
                settings.Tags.SavePlaylistAsCompilation ||
                settings.CreateStructurePlaylist);
    }

    private static string ResolveFilenameTemplate(DeezSpoTagSettings settings)
    {
        return settings.TracknameTemplate;
    }

    private static Artist? ResolveArtistForFolder(Track track)
    {
        var albumArtist = track.Album?.MainArtist;
        return IsKnownArtist(albumArtist) ? albumArtist : track.MainArtist;
    }

    private static bool IsKnownArtist(Artist? artist)
    {
        if (artist == null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return false;
        }

        return artist.Name != UnknownValue && artist.Name != UnknownArtist;
    }

    private static (string FilePath, string Filename) ApplyFilenameSubfolder(string filepath, string filename)
    {
        if (!filename.Contains('/'))
        {
            return (filepath, filename);
        }

        var slashIndex = filename.IndexOf('/');
        var subPath = filename[..slashIndex];
        var updatedFilePath = $"{filepath}/{subPath}";
        var updatedFilename = filename[(slashIndex + 1)..];
        return (updatedFilePath, updatedFilename);
    }

    #region Download Utilities

    public static bool CheckShouldDownload(
        string filename,
        string filepath,
        string extension,
        string writepath,
        OverwriteOption overwriteFile,
        Track track)
        => DownloadUtils.CheckShouldDownload(filename, filepath, extension, writepath, overwriteFile, track);

    public static Task TagTrackAsync(
        string extension,
        string writepath,
        Track track,
        TagSettings tags,
        AudioTagger tagger) =>
        DownloadUtils.TagTrackAsync(extension, writepath, track, tags, tagger);

    public static string GetExtensionForFormat(TrackFormat format) =>
        DownloadUtils.GetExtensionForFormat(format);

    public static string GenerateUniqueFilename(string basePath, string filename, string extension) =>
        DownloadUtils.GenerateUniqueFilename(basePath, filename, extension);

    public static string ValidateDownloadPath(string path) =>
        DownloadUtils.ValidateDownloadPath(path);

    public static void EnsureDirectoryExists(string path) =>
        DownloadUtils.EnsureDirectoryExists(path);

    public static string GetBitrateLabel(TrackFormat format) =>
        DownloadUtils.GetBitrateLabel(format);

    public static bool IsLosslessFormat(TrackFormat format) =>
        DownloadUtils.IsLosslessFormat(format);

    public static bool Is360Format(TrackFormat format) =>
        DownloadUtils.Is360Format(format);

    #endregion
}
