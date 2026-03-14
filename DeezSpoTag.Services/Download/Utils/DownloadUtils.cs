using DeezSpoTag.Core.Enums;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Download utilities ported from deezspotag downloadUtils.ts
/// </summary>
public static class DownloadUtils
{
    private static readonly string[] AlternateAudioExtensions = { ".mp3", ".flac", ".opus", ".m4a" };

    /// <summary>
    /// Check if a track should be downloaded based on overwrite settings (port of checkShouldDownload)
    /// </summary>
    public static bool CheckShouldDownload(
        string filename,
        string filepath,
        string extension,
        string writepath,
        OverwriteOption overwriteFile,
        Track track)
    {
        if (CanAlwaysDownload(overwriteFile))
        {
            return true;
        }

        var trackAlreadyDownloaded = File.Exists(writepath);

        if (ShouldSkipExistingFile(overwriteFile, trackAlreadyDownloaded))
        {
            return false;
        }

        if (ShouldSkipWhenAnyAudioExtensionExists(overwriteFile, trackAlreadyDownloaded, filepath, filename))
        {
            return false;
        }

        if (ShouldOverwriteLowerBitrate(overwriteFile, trackAlreadyDownloaded, extension, writepath, track))
        {
            return true;
        }

        return !trackAlreadyDownloaded;
    }

    private static bool CanAlwaysDownload(OverwriteOption overwriteFile)
        => overwriteFile is OverwriteOption.Overwrite or OverwriteOption.KeepBoth;

    private static bool ShouldSkipExistingFile(OverwriteOption overwriteFile, bool trackAlreadyDownloaded)
        => trackAlreadyDownloaded && overwriteFile == OverwriteOption.DontOverwrite;

    private static bool ShouldSkipWhenAnyAudioExtensionExists(
        OverwriteOption overwriteFile,
        bool trackAlreadyDownloaded,
        string filepath,
        string filename)
    {
        if (trackAlreadyDownloaded || overwriteFile != OverwriteOption.DontCheckExt)
        {
            return false;
        }

        var baseFilename = Path.Join(filepath, filename);
        return AlternateAudioExtensions.Any(ext => File.Exists(baseFilename + ext));
    }

    private static bool ShouldOverwriteLowerBitrate(
        OverwriteOption overwriteFile,
        bool trackAlreadyDownloaded,
        string extension,
        string writepath,
        Track track)
    {
        if (!trackAlreadyDownloaded
            || overwriteFile != OverwriteOption.OnlyLowerBitrates
            || extension != ".mp3")
        {
            return false;
        }

        try
        {
            return HasLowerMp3Bitrate(writepath, track);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool HasLowerMp3Bitrate(string writepath, Track track)
    {
        if (track.Duration <= 0)
        {
            return false;
        }

        var stats = new FileInfo(writepath);
        var fileSizeKb = (stats.Length * 8d) / 1024d;
        var bitrateApprox = fileSizeKb / track.Duration;
        return track.Bitrate == 3 && bitrateApprox < 310;
    }

    /// <summary>
    /// Tag a track file (port of tagTrack function)
    /// </summary>
    public static async Task TagTrackAsync(
        string extension,
        string writepath,
        Track track,
        TagSettings tags,
        AudioTagger tagger)
    {
        // Both MP3 and FLAC are handled by TagLib in AudioTagger
        await tagger.TagTrackAsync(extension, writepath, track, tags);
    }

    /// <summary>
    /// Get file extension for track format
    /// </summary>
    public static string GetExtensionForFormat(TrackFormat format)
    {
        return format switch
        {
            TrackFormat.FLAC => ".flac",
            TrackFormat.MP3_320 => ".mp3",
            TrackFormat.MP3_128 => ".mp3",
            TrackFormat.MP4_RA1 => ".mp4",
            TrackFormat.MP4_RA2 => ".mp4",
            TrackFormat.MP4_RA3 => ".mp4",
            _ => ".mp3"
        };
    }

    /// <summary>
    /// Generate safe filename by replacing illegal characters
    /// </summary>
    public static string FixName(string text, string replacementChar = "_")
    {
        return CjkFilenameSanitizer.SanitizeSegment(
            text,
            fallback: string.Empty,
            replacement: replacementChar,
            collapseWhitespace: false,
            trimTrailingDotsAndSpaces: false);
    }

    /// <summary>
    /// Fix long filenames by truncating them
    /// </summary>
    public static string FixLongName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        if (name.Contains('/'))
        {
            var parts = name.Split('/');
            var fixedParts = parts.Select(FixLongName);
            return string.Join("/", fixedParts);
        }

        // Limit to 200 Unicode scalar values to avoid filesystem issues.
        return CjkFilenameSanitizer.TruncateByRuneCount(name, 200);
    }

    /// <summary>
    /// Remove trailing dots, spaces, and newlines (port of antiDot)
    /// </summary>
    public static string AntiDot(string str)
    {
        if (string.IsNullOrEmpty(str))
            return "dot";

        while (str.Length > 0 && (str[^1] == '.' || str[^1] == ' ' || str[^1] == '\n'))
        {
            str = str[..^1];
        }

        return str.Length < 1 ? "dot" : str;
    }

    /// <summary>
    /// Pad track numbers (port of pad function)
    /// </summary>
    public static string Pad(int num, int maxVal, DeezSpoTagSettings settings)
    {
        if (!settings.PadTracks)
            return num.ToString();

        var paddingSize = settings.PaddingSize == 0 
            ? maxVal.ToString().Length 
            : settings.PaddingSize;

        if (settings.PadSingleDigit && paddingSize == 1)
            paddingSize = 2;

        return num.ToString().PadLeft(paddingSize, '0');
    }

    /// <summary>
    /// Create directory if it doesn't exist
    /// </summary>
    public static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    /// <summary>
    /// Get bitrate label for display
    /// </summary>
    public static string GetBitrateLabel(TrackFormat format)
    {
        return format switch
        {
            TrackFormat.MP4_RA3 => "360 HQ",
            TrackFormat.MP4_RA2 => "360 MQ", 
            TrackFormat.MP4_RA1 => "360 LQ",
            TrackFormat.FLAC => "FLAC",
            TrackFormat.MP3_320 => "320",
            TrackFormat.MP3_128 => "128",
            _ => "128"
        };
    }

    /// <summary>
    /// Check if a format is lossless
    /// </summary>
    public static bool IsLosslessFormat(TrackFormat format)
    {
        return format == TrackFormat.FLAC;
    }

    /// <summary>
    /// Check if a format is 360 Reality Audio
    /// </summary>
    public static bool Is360Format(TrackFormat format)
    {
        return format is TrackFormat.MP4_RA1 or TrackFormat.MP4_RA2 or TrackFormat.MP4_RA3;
    }

    /// <summary>
    /// Generate unique filename if file already exists
    /// </summary>
    public static string GenerateUniqueFilename(string basePath, string filename, string extension)
    {
        var fullPath = Path.Join(basePath, filename + extension);
        
        if (!System.IO.File.Exists(fullPath))
            return filename;

        var counter = 1;
        string uniqueFilename;
        
        do
        {
            uniqueFilename = $"{filename} ({counter})";
            fullPath = Path.Join(basePath, uniqueFilename + extension);
            counter++;
        } 
        while (System.IO.File.Exists(fullPath));

        return uniqueFilename;
    }

    /// <summary>
    /// Validate and sanitize download path
    /// </summary>
    public static string ValidateDownloadPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        try
        {
            // Ensure the path is absolute
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(path);
            }

            // Create directory if it doesn't exist
            EnsureDirectoryExists(path);
            
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Fallback to Music folder if path is invalid
            return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }
    }
}
