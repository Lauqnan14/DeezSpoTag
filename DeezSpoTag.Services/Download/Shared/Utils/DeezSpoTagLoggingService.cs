using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared.Utils;

/// <summary>
/// Enhanced logging service for deezspotag operations
/// Ported from: Logging logic in deezspotag downloader.ts afterDownloadSingle/afterDownloadCollection
/// </summary>
public class DeezSpoTagLoggingService
{
    private const string UnknownValue = "Unknown";
    private readonly ILogger<DeezSpoTagLoggingService> _logger;

    public DeezSpoTagLoggingService(ILogger<DeezSpoTagLoggingService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Create error log file for failed downloads
    /// Ported from: Error logging logic in deezspotag downloader.ts
    /// </summary>
    public async Task CreateErrorLogAsync(DeezSpoTagSettings settings, string extrasPath, List<object> errors)
    {
        try
        {
            if (!settings.LogErrors || errors.Count == 0)
            {
                return;
            }

            _logger.LogDebug("Creating error log with {ErrorCount} errors", errors.Count);

            var errorLogPath = Path.Join(extrasPath, "errors.txt");
            var errorLines = errors
                .Select(FormatErrorForLog)
                .Where(static errorLine => !string.IsNullOrEmpty(errorLine))
                .Select(static errorLine => errorLine!)
                .ToList();

            if (errorLines.Count > 0)
            {
                await File.WriteAllLinesAsync(errorLogPath, errorLines);
                _logger.LogInformation("Created error log: {ErrorLogPath} with {ErrorCount} errors", 
                    errorLogPath, errorLines.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error creating error log file");
        }
    }

    /// <summary>
    /// Create searched tracks log file
    /// Ported from: Searched logging logic in deezspotag downloader.ts
    /// </summary>
    public async Task CreateSearchedLogAsync(DeezSpoTagSettings settings, string extrasPath, List<object> searchedTracks)
    {
        try
        {
            if (!settings.LogSearched || searchedTracks.Count == 0)
            {
                return;
            }

            _logger.LogDebug("Creating searched log with {SearchedCount} tracks", searchedTracks.Count);

            var searchedLogPath = Path.Join(extrasPath, "searched.txt");
            var searchedLines = searchedTracks
                .Select(FormatSearchedTrackForLog)
                .Where(static trackLine => !string.IsNullOrEmpty(trackLine))
                .Select(static trackLine => trackLine!)
                .ToList();

            if (searchedLines.Count > 0)
            {
                await File.WriteAllLinesAsync(searchedLogPath, searchedLines);
                _logger.LogInformation("Created searched log: {SearchedLogPath} with {SearchedCount} tracks", 
                    searchedLogPath, searchedLines.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error creating searched log file");
        }
    }

    /// <summary>
    /// Append to searched tracks log file (for single downloads)
    /// Ported from: Single track searched logging in deezspotag downloader.ts
    /// </summary>
    public async Task AppendToSearchedLogAsync(DeezSpoTagSettings settings, string extrasPath, object trackData)
    {
        try
        {
            if (!settings.LogSearched)
            {
                return;
            }

            var searchedLogPath = Path.Join(extrasPath, "searched.txt");
            var trackLine = FormatSearchedTrackForLog(trackData);
            
            if (string.IsNullOrEmpty(trackLine))
            {
                return;
            }

            // Check if track is already in the log
            if (System.IO.File.Exists(searchedLogPath))
            {
                var existingContent = await File.ReadAllTextAsync(searchedLogPath);
                if (existingContent.Contains(trackLine))
                {
                    _logger.LogDebug("Track already exists in searched log: {TrackLine}", trackLine);
                    return;
                }
            }

            // Append to file
            var content = System.IO.File.Exists(searchedLogPath) ? 
                await File.ReadAllTextAsync(searchedLogPath) : "";
            
            if (!string.IsNullOrEmpty(content) && !content.EndsWith(Environment.NewLine))
            {
                content += Environment.NewLine;
            }
            
            content += trackLine + Environment.NewLine;
            
            await System.IO.File.WriteAllTextAsync(searchedLogPath, content);
            
            _logger.LogDebug("Appended searched track to log: {TrackLine}", trackLine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error appending to searched log file");
        }
    }

    /// <summary>
    /// Create M3U8 playlist file
    /// Ported from: M3U8 creation logic in deezspotag downloader.ts
    /// </summary>
    public async Task CreateM3U8PlaylistAsync(DeezSpoTagSettings settings, DeezSpoTagDownloadObject downloadObject, List<string> filenames)
    {
        try
        {
            if (!settings.CreateM3U8File || filenames.Count == 0)
            {
                return;
            }

            _logger.LogDebug("Creating M3U8 playlist for {ObjectType}: {Title}", downloadObject.Type, downloadObject.Title);

            var playlistName = GeneratePlaylistName(settings, downloadObject);
            var playlistPath = Path.Join(downloadObject.ExtrasPath ?? "", $"{playlistName}.m3u8");

            // Create M3U8 content
            var m3u8Lines = new List<string>
            {
                "#EXTM3U",
                ""
            };

            foreach (var filename in filenames.Where(f => !string.IsNullOrEmpty(f)))
            {
                m3u8Lines.Add(filename);
            }

            await File.WriteAllLinesAsync(playlistPath, m3u8Lines);
            
            _logger.LogInformation("Created M3U8 playlist: {PlaylistPath} with {TrackCount} tracks", 
                playlistPath, filenames.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error creating M3U8 playlist file");
        }
    }

    /// <summary>
    /// Log download statistics
    /// </summary>
    public void LogDownloadStatistics(DeezSpoTagDownloadObject downloadObject)
    {
        try
        {
            var totalTracks = downloadObject.Size;
            var downloadedTracks = downloadObject.Downloaded;
            var failedTracks = downloadObject.Failed;
            var successRate = totalTracks > 0 ? (double)downloadedTracks / totalTracks * 100 : 0;

            _logger.LogInformation(
                "Download completed for {ObjectType} '{Title}': {Downloaded}/{Total} tracks downloaded ({SuccessRate:F1}% success rate), {Failed} failed",
                downloadObject.Type, downloadObject.Title, downloadedTracks, totalTracks, successRate, failedTracks);

            if (downloadObject.Errors.Count > 0)
            {
                _logger.LogWarning("Download had {ErrorCount} errors:", downloadObject.Errors.Count);
                foreach (var error in downloadObject.Errors.Take(5)) // Log first 5 errors
                {
                    _logger.LogWarning("  - {ErrorMessage}", FormatErrorForLog(error));
                }
                
                if (downloadObject.Errors.Count > 5)
                {
                    _logger.LogWarning("  ... and {AdditionalErrors} more errors", downloadObject.Errors.Count - 5);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error logging download statistics");
        }
    }

    /// <summary>
    /// Format error for log file
    /// Ported from: Error formatting logic in deezspotag downloader.ts
    /// </summary>
    private string FormatErrorForLog(object error)
    {
        try
        {
            if (error == null) return "";

            // Handle different error object types
            if (error is Dictionary<string, object> errorDict)
            {
                var id = errorDict.GetValueOrDefault("id")?.ToString() ?? "0";
                var artist = "";
                var title = "";
                var message = errorDict.GetValueOrDefault("message")?.ToString() ?? "Unknown error";

                // Extract artist and title from data
                if (errorDict.TryGetValue("data", out var dataObj) && dataObj is Dictionary<string, object> data)
                {
                    artist = data.GetValueOrDefault("artist")?.ToString() ?? UnknownValue;
                    title = data.GetValueOrDefault("title")?.ToString() ?? UnknownValue;
                }

                return $"{id} | {artist} - {title} | {message}";
            }

            // Fallback to string representation
            return error.ToString() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error formatting error for log");
            return "Error formatting failed";
        }
    }

    /// <summary>
    /// Format searched track for log file
    /// Ported from: Searched track formatting logic in deezspotag downloader.ts
    /// </summary>
    private string FormatSearchedTrackForLog(object trackData)
    {
        try
        {
            if (trackData == null) return "";

            // Handle different track data types
            if (trackData is Dictionary<string, object> trackDict)
            {
                var artist = trackDict.GetValueOrDefault("artist")?.ToString() ?? UnknownValue;
                var title = trackDict.GetValueOrDefault("title")?.ToString() ?? UnknownValue;
                return $"{artist} - {title}";
            }

            // Fallback to string representation
            return trackData.ToString() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error formatting searched track for log");
            return "Track formatting failed";
        }
    }

    /// <summary>
    /// Generate playlist name for M3U8 file
    /// Ported from: Playlist name generation in deezspotag downloader.ts
    /// </summary>
    private string GeneratePlaylistName(DeezSpoTagSettings settings, DeezSpoTagDownloadObject downloadObject)
    {
        try
        {
            var template = settings.PlaylistFilenameTemplate;
            
            if (string.IsNullOrEmpty(template))
            {
                template = "playlist";
            }

            // Replace template variables
            var playlistName = template
                .Replace("%title%", downloadObject.Title ?? UnknownValue)
                .Replace("%artist%", downloadObject.Artist ?? UnknownValue)
                .Replace("%type%", downloadObject.Type ?? "unknown")
                .Replace("%id%", downloadObject.Id ?? "0");

            return CjkFilenameSanitizer.SanitizeSegment(
                playlistName,
                fallback: "playlist",
                replacement: "_",
                collapseWhitespace: true,
                trimTrailingDotsAndSpaces: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error generating playlist name, using default");
            return "playlist";
        }
    }

    /// <summary>
    /// Clean up old log files
    /// </summary>
    public void CleanupOldLogs(string extrasPath, int maxAgeInDays = 30)
    {
        try
        {
            if (!Directory.Exists(extrasPath))
            {
                return;
            }

            var cutoffDate = DateTime.UtcNow.AddDays(-maxAgeInDays);
            var logFiles = Directory.GetFiles(extrasPath, "*.txt")
                .Where(f => Path.GetFileName(f).StartsWith("errors") || Path.GetFileName(f).StartsWith("searched"))
                .Where(f => File.GetCreationTimeUtc(f) < cutoffDate);

            var deletedCount = 0;
            foreach (var logFile in logFiles)
            {
                try
                {
                    File.Delete(logFile);
                    deletedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to delete old log file: {LogFile}", logFile);
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleaned up {DeletedCount} old log files", deletedCount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error cleaning up old log files");
        }
    }
}
