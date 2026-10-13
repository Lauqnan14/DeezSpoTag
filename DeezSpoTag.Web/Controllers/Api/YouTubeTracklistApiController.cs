using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Services.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/youtube/tracklist")]
public sealed class YouTubeTracklistApiController(YouTubeMusicSearchClient client, DeezSpoTagSettingsService settingsService, ILogger<YouTubeTracklistApiController> logger) : ControllerBase
{
    [HttpGet("parse-link")]
    public IActionResult ParseLink([FromQuery] string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Host is not ("music.youtube.com" or "www.youtube.com" or "youtube.com" or "youtu.be"))
            return BadRequest(new { error = "Unsupported YouTube Music link." });
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var id = uri.Host == "youtu.be" ? uri.AbsolutePath.Trim('/') : (query.TryGetValue("v", out var video) ? video.ToString() : "");
        if (uri.Host == "youtu.be" || uri.AbsolutePath == "/watch")
        {
            try { DeezSpoTag.Web.Services.YouTubePlaybackResolver.ValidateVideoId(id); }
            catch (ArgumentException) { return BadRequest(new { error = "Invalid YouTube Music song link." }); }
            return Ok(new { id, type = "track" });
        }
        if (uri.AbsolutePath == "/playlist")
        {
            id = query.TryGetValue("list", out var list) ? list.ToString() : "";
            if (System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{10,200}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                return Ok(new { id, type = "playlist" });
        }
        if (uri.AbsolutePath.StartsWith("/browse/", StringComparison.Ordinal))
        {
            id = uri.AbsolutePath[8..];
            if (System.Text.RegularExpressions.Regex.IsMatch(id, "^MPRE[A-Za-z0-9_-]{6,200}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                return Ok(new { id, type = "album" });
        }
        return BadRequest(new { error = "Unsupported YouTube Music song or collection link." });
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string id, [FromQuery] string type = "playlist", CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = settingsService.LoadSettings();
            var country = (string.IsNullOrWhiteSpace(settings.YouTubeMusicCountry) ? settings.DeezerCountry : settings.YouTubeMusicCountry).Trim().ToUpperInvariant();
            object tracklist;
            if (type.Trim().Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                DeezSpoTag.Web.Services.YouTubePlaybackResolver.ValidateVideoId(id);
                var song = await client.GetQueueSongMetadataAsync(id, country, settings.DeezerLanguage.Trim(), cancellationToken);
                var cover = "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg";
                tracklist = new { title = song.Title, nb_tracks = 1, creator = new { name = "YouTube Music" }, cover_big = cover,
                    tracks = new[] { new { id = "", videoId = song.VideoId, title = song.Title, artist = new { name = song.Artist },
                        album = new { title = song.Album, cover_medium = cover, cover_big = cover }, duration = song.DurationSeconds,
                        link = "https://music.youtube.com/watch?v=" + id, preview = "/api/youtube/browse/stream?id=" + id } } };
            }
            else tracklist = await client.GetCollectionAsync(id, type.Trim().ToLowerInvariant(), country, settings.DeezerLanguage.Trim(), cancellationToken);
            return Ok(new { available = true, tracklist });
        }
        catch (ArgumentException ex) { return BadRequest(new { available = false, error = ex.Message }); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "YouTube Music collection load failed.");
            return StatusCode(502, new { available = false, error = "YouTube Music collection unavailable." });
        }
    }
}
