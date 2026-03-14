using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/youtube/tracklist")]
[Authorize]
public sealed class YouTubeTracklistApiController : ControllerBase
{
    private static readonly TimeSpan PlaylistIdRegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex PlaylistIdRegex = new(
        @"^[A-Za-z0-9_-]{10,}$",
        RegexOptions.Compiled,
        PlaylistIdRegexTimeout);
    private static readonly TimeSpan YtDlpTimeout = TimeSpan.FromSeconds(45);

    private readonly ILogger<YouTubeTracklistApiController> _logger;

    public YouTubeTracklistApiController(ILogger<YouTubeTracklistApiController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string id,
        [FromQuery] string type = "playlist",
        CancellationToken cancellationToken = default)
    {
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(normalizedType, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { available = false, error = $"Unsupported type '{type}'." });
        }

        var playlistId = ResolvePlaylistId(id);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BadRequest(new { available = false, error = "YouTube playlist id is required." });
        }

        var playlistUrl = $"https://music.youtube.com/playlist?list={playlistId}";
        try
        {
            var rawJson = await FetchYtDlpPlaylistJsonAsync(playlistUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return NotFound(new { available = false, error = "YouTube playlist unavailable." });
            }

            if (!TryBuildTracklistPayload(playlistId, rawJson, out var tracklist))
            {
                return NotFound(new { available = false, error = "YouTube playlist has no tracks." });
            }

            return Ok(new
            {
                available = true,
                tracklist
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "YouTube playlist tracklist fetch failed for playlist id {PlaylistId}.", playlistId);
            return StatusCode(500, new { available = false, error = "Failed to load YouTube playlist." });
        }
    }

    private async Task<string?> FetchYtDlpPlaylistJsonAsync(string playlistUrl, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("--flat-playlist");
        process.StartInfo.ArgumentList.Add("--dump-single-json");
        process.StartInfo.ArgumentList.Add("--no-warnings");
        process.StartInfo.ArgumentList.Add("--no-call-home");
        process.StartInfo.ArgumentList.Add(playlistUrl);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(YtDlpTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            _logger.LogWarning("yt-dlp timed out while fetching YouTube playlist {PlaylistUrl}.", playlistUrl);
            return null;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "yt-dlp failed for {PlaylistUrl}. exitCode={ExitCode} stderr={StdErr}",
                playlistUrl,
                process.ExitCode,
                TrimStdErr(stderr));
            return null;
        }

        return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
    }

    private static bool TryBuildTracklistPayload(string playlistId, string rawJson, out object tracklist)
    {
        tracklist = new { };

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var playlistTitle = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(playlistTitle))
        {
            playlistTitle = "YouTube Playlist";
        }

        var creatorName = GetString(root, "uploader");
        if (string.IsNullOrWhiteSpace(creatorName))
        {
            creatorName = "YouTube";
        }

        var description = GetString(root, "description");
        var coverUrl = ExtractCoverUrl(root);

        var tracks = new List<object>();
        if (root.TryGetProperty("entries", out var entriesElement)
            && entriesElement.ValueKind == JsonValueKind.Array)
        {
            var trackPosition = 0;
            foreach (var entry in entriesElement.EnumerateArray())
            {
                var videoId = GetString(entry, "id");
                var sourceUrl = BuildTrackUrl(
                    videoId,
                    GetString(entry, "url"),
                    GetString(entry, "webpage_url"));
                if (string.IsNullOrWhiteSpace(sourceUrl))
                {
                    continue;
                }

                var title = GetString(entry, "title");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = $"Track {trackPosition + 1}";
                }

                var artistName = GetString(entry, "uploader");
                if (string.IsNullOrWhiteSpace(artistName))
                {
                    artistName = creatorName;
                }

                trackPosition++;
                tracks.Add(new
                {
                    id = videoId,
                    title,
                    duration = GetInt(entry, "duration"),
                    track_position = trackPosition,
                    link = sourceUrl,
                    sourceUrl,
                    artist = new { id = string.Empty, name = artistName },
                    album = new { id = playlistId, title = playlistTitle, cover_medium = coverUrl }
                });
            }
        }

        if (tracks.Count == 0)
        {
            return false;
        }

        tracklist = new
        {
            id = playlistId,
            title = playlistTitle,
            description,
            cover_big = coverUrl,
            cover_xl = coverUrl,
            picture_big = coverUrl,
            picture_xl = coverUrl,
            creator = new { name = creatorName },
            nb_tracks = tracks.Count,
            tracks
        };
        return true;
    }

    private static string ResolvePlaylistId(string? rawId)
    {
        var candidate = (rawId ?? string.Empty).Trim();
        if (PlaylistIdRegex.IsMatch(candidate))
        {
            return candidate;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            var listId = (uri.Query.Length > 0 ? uri.Query : string.Empty)
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(parts => parts.Length == 2
                                         && string.Equals(parts[0], "list", StringComparison.OrdinalIgnoreCase))?[1];

            var decoded = Uri.UnescapeDataString(listId ?? string.Empty);
            return PlaylistIdRegex.IsMatch(decoded) ? decoded : string.Empty;
        }

        return string.Empty;
    }

    private static string BuildTrackUrl(string videoId, string directUrl, string pageUrl)
    {
        if (Uri.TryCreate(directUrl, UriKind.Absolute, out _))
        {
            return directUrl;
        }

        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out _))
        {
            return pageUrl;
        }

        if (!string.IsNullOrWhiteSpace(videoId))
        {
            return $"https://music.youtube.com/watch?v={Uri.EscapeDataString(videoId)}";
        }

        return string.Empty;
    }

    private static string ExtractCoverUrl(JsonElement root)
    {
        var directThumbnail = GetString(root, "thumbnail");
        if (!string.IsNullOrWhiteSpace(directThumbnail))
        {
            return directThumbnail;
        }

        if (!root.TryGetProperty("thumbnails", out var thumbnailsElement)
            || thumbnailsElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string best = string.Empty;
        var bestWidth = -1;
        foreach (var thumbnail in thumbnailsElement.EnumerateArray())
        {
            var url = GetString(thumbnail, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var width = GetInt(thumbnail, "width");
            if (width >= bestWidth)
            {
                bestWidth = width;
                best = url;
            }
        }

        return best;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return Math.Max(0, number);
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return Math.Max(0, parsed);
        }

        return 0;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string TrimStdErr(string? stderr)
    {
        var value = (stderr ?? string.Empty).Trim();
        if (value.Length <= 400)
        {
            return value;
        }

        return value[..400];
    }
}
