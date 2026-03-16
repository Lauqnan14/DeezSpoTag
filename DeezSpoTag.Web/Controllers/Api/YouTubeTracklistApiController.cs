using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Web.Services;
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
        return await YtDlpPlaylistJsonFetcher.FetchAsync(playlistUrl, "YouTube", _logger, cancellationToken);
    }

    private static bool TryBuildTracklistPayload(string playlistId, string rawJson, out object tracklist)
    {
        tracklist = new { };

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var metadata = BuildPlaylistMetadata(playlistId, root);
        var tracks = BuildTrackEntries(root, metadata);
        if (tracks.Count == 0)
        {
            return false;
        }

        tracklist = new
        {
            id = metadata.PlaylistId,
            title = metadata.PlaylistTitle,
            description = metadata.Description,
            cover_big = metadata.CoverUrl,
            cover_xl = metadata.CoverUrl,
            picture_big = metadata.CoverUrl,
            picture_xl = metadata.CoverUrl,
            creator = new { name = metadata.CreatorName },
            nb_tracks = tracks.Count,
            tracks
        };
        return true;
    }

    private static PlaylistMetadata BuildPlaylistMetadata(string playlistId, JsonElement root)
    {
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

        return new PlaylistMetadata(
            playlistId,
            playlistTitle,
            creatorName,
            GetString(root, "description"),
            ExtractCoverUrl(root));
    }

    private static List<object> BuildTrackEntries(JsonElement root, PlaylistMetadata metadata)
    {
        var tracks = new List<object>();
        if (!root.TryGetProperty("entries", out var entriesElement)
            || entriesElement.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        var trackPosition = 0;
        foreach (var entry in entriesElement.EnumerateArray())
        {
            var sourceUrl = ResolveTrackUrl(entry);
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                continue;
            }

            trackPosition++;
            tracks.Add(BuildTrackEntry(entry, sourceUrl, trackPosition, metadata));
        }

        return tracks;
    }

    private static string ResolveTrackUrl(JsonElement entry)
    {
        return BuildTrackUrl(
            GetString(entry, "id"),
            GetString(entry, "url"),
            GetString(entry, "webpage_url"));
    }

    private static object BuildTrackEntry(JsonElement entry, string sourceUrl, int trackPosition, PlaylistMetadata metadata)
    {
        var videoId = GetString(entry, "id");
        var title = GetString(entry, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"Track {trackPosition}";
        }

        var artistName = GetString(entry, "uploader");
        if (string.IsNullOrWhiteSpace(artistName))
        {
            artistName = metadata.CreatorName;
        }

        return new
        {
            id = videoId,
            title,
            duration = GetInt(entry, "duration"),
            track_position = trackPosition,
            link = sourceUrl,
            sourceUrl,
            artist = new { id = string.Empty, name = artistName },
            album = new { id = metadata.PlaylistId, title = metadata.PlaylistTitle, cover_medium = metadata.CoverUrl }
        };
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

    private sealed record PlaylistMetadata(
        string PlaylistId,
        string PlaylistTitle,
        string CreatorName,
        string Description,
        string CoverUrl);
}
