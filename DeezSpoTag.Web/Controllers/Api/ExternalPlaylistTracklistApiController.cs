using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Web.Services.LinkMapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/external/playlist/tracklist")]
[Authorize]
public sealed class ExternalPlaylistTracklistApiController : ControllerBase
{
    private static readonly TimeSpan YtDlpTimeout = TimeSpan.FromSeconds(45);
    private static readonly HashSet<string> SupportedSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "soundcloud",
        "tidal",
        "qobuz",
        "bandcamp",
        "pandora"
    };

    private readonly ExternalLinkClassifier _classifier;
    private readonly ILogger<ExternalPlaylistTracklistApiController> _logger;

    public ExternalPlaylistTracklistApiController(
        ExternalLinkClassifier classifier,
        ILogger<ExternalPlaylistTracklistApiController> logger)
    {
        _classifier = classifier;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? source,
        [FromQuery] string? url,
        [FromQuery] string type = "playlist",
        CancellationToken cancellationToken = default)
    {
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(normalizedType, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { available = false, error = $"Unsupported type '{type}'." });
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var playlistUri))
        {
            return BadRequest(new { available = false, error = "External playlist URL is required." });
        }

        if (!IsHttp(playlistUri))
        {
            return BadRequest(new { available = false, error = "Only HTTP/HTTPS playlist URLs are supported." });
        }

        var normalizedUrl = playlistUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var normalizedSource = NormalizeSource(source);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            normalizedSource = ClassifySource(normalizedUrl);
        }

        if (string.IsNullOrWhiteSpace(normalizedSource) || !SupportedSources.Contains(normalizedSource))
        {
            return BadRequest(new
            {
                available = false,
                error = "Unsupported external playlist source. Supported sources: SoundCloud, Tidal, Qobuz, Bandcamp, Pandora."
            });
        }

        if (!IsPlaylistLikeUrl(playlistUri, normalizedSource))
        {
            return BadRequest(new
            {
                available = false,
                error = $"{FormatSourceLabel(normalizedSource)} link is not recognized as a playlist URL."
            });
        }

        try
        {
            var rawJson = await FetchYtDlpPlaylistJsonAsync(normalizedUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return NotFound(new
                {
                    available = false,
                    error = $"{FormatSourceLabel(normalizedSource)} playlist is unavailable."
                });
            }

            if (!TryBuildTracklistPayload(normalizedSource, normalizedUrl, rawJson, out var tracklist))
            {
                return NotFound(new
                {
                    available = false,
                    error = $"{FormatSourceLabel(normalizedSource)} playlist has no tracks."
                });
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
            _logger.LogWarning(ex, "External playlist tracklist fetch failed. source={Source} url={Url}", normalizedSource, normalizedUrl);
            return StatusCode(500, new { available = false, error = "Failed to load external playlist." });
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
            _logger.LogWarning("yt-dlp timed out while fetching external playlist {PlaylistUrl}.", playlistUrl);
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

    private static bool TryBuildTracklistPayload(string source, string sourceUrl, string rawJson, out object tracklist)
    {
        tracklist = new { };
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var playlistId = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = BuildStableId(sourceUrl);
        }

        var playlistTitle = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(playlistTitle))
        {
            playlistTitle = $"{FormatSourceLabel(source)} Playlist";
        }

        var creatorName = FirstNonEmpty(
            GetString(root, "uploader"),
            GetString(root, "artist"),
            GetString(root, "channel"),
            GetString(root, "creator"),
            FormatSourceLabel(source));
        var description = GetString(root, "description");
        var coverUrl = ExtractCoverUrl(root);

        var tracks = new List<object>();
        if (root.TryGetProperty("entries", out var entriesElement)
            && entriesElement.ValueKind == JsonValueKind.Array)
        {
            var position = 0;
            foreach (var entry in entriesElement.EnumerateArray())
            {
                var entryUrl = ResolveEntryUrl(source, entry);
                if (string.IsNullOrWhiteSpace(entryUrl))
                {
                    continue;
                }

                position++;
                var entryId = GetString(entry, "id");
                if (string.IsNullOrWhiteSpace(entryId))
                {
                    entryId = BuildStableId(entryUrl);
                }

                var title = GetString(entry, "title");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = $"Track {position}";
                }

                var artistName = FirstNonEmpty(
                    GetString(entry, "artist"),
                    GetString(entry, "uploader"),
                    GetString(entry, "channel"),
                    GetString(entry, "creator"),
                    creatorName);

                tracks.Add(new
                {
                    id = entryId,
                    title,
                    duration = GetInt(entry, "duration"),
                    track_position = position,
                    link = entryUrl,
                    sourceUrl = entryUrl,
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

    private static string ResolveEntryUrl(string source, JsonElement entry)
    {
        var webpageUrl = GetString(entry, "webpage_url");
        if (Uri.TryCreate(webpageUrl, UriKind.Absolute, out _))
        {
            return webpageUrl;
        }

        var directUrl = GetString(entry, "url");
        if (Uri.TryCreate(directUrl, UriKind.Absolute, out _))
        {
            return directUrl;
        }

        var id = GetString(entry, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        return source.ToLowerInvariant() switch
        {
            "tidal" when id.All(char.IsDigit) => $"https://tidal.com/browse/track/{id}",
            "soundcloud" => BuildSoundCloudFallbackUrl(entry, id),
            _ => string.Empty
        };
    }

    private static string BuildSoundCloudFallbackUrl(JsonElement entry, string id)
    {
        var uploaderId = GetString(entry, "uploader_id");
        if (!string.IsNullOrWhiteSpace(uploaderId))
        {
            return $"https://soundcloud.com/{Uri.EscapeDataString(uploaderId)}/{Uri.EscapeDataString(id)}";
        }

        return string.Empty;
    }

    private static string BuildStableId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"ext-{hex[..12]}";
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

        var bestWidth = -1;
        var bestUrl = string.Empty;
        foreach (var thumbnail in thumbnailsElement.EnumerateArray())
        {
            var url = GetString(thumbnail, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var width = GetInt(thumbnail, "width");
            if (width > bestWidth)
            {
                bestWidth = width;
                bestUrl = url;
            }
        }

        return bestUrl;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return 0;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var value) => value,
            _ => 0
        };
    }

    private static bool IsHttp(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKillProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string TrimStdErr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        const int maxLength = 400;
        var trimmed = stderr.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string NormalizeSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "soundcloud" => "soundcloud",
            "tidal" => "tidal",
            "qobuz" => "qobuz",
            "bandcamp" => "bandcamp",
            "pandora" => "pandora",
            _ => string.Empty
        };
    }

    private string ClassifySource(string normalizedUrl)
    {
        var classified = _classifier.Classify(normalizedUrl);
        return classified switch
        {
            ExternalLinkSource.SoundCloud => "soundcloud",
            ExternalLinkSource.Tidal => "tidal",
            ExternalLinkSource.Qobuz => "qobuz",
            ExternalLinkSource.Bandcamp => "bandcamp",
            ExternalLinkSource.Pandora => "pandora",
            _ => string.Empty
        };
    }

    private static bool IsPlaylistLikeUrl(Uri uri, string source)
    {
        var path = (uri.AbsolutePath ?? string.Empty).ToLowerInvariant();
        return source.ToLowerInvariant() switch
        {
            "soundcloud" => path.Contains("/sets/", StringComparison.Ordinal),
            "tidal" => path.Contains("/playlist/", StringComparison.Ordinal) || path.Contains("/mix/", StringComparison.Ordinal),
            "qobuz" => path.Contains("/playlist/", StringComparison.Ordinal),
            "bandcamp" => path.Contains("/album/", StringComparison.Ordinal),
            "pandora" => path.Contains("/playlist/", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string FormatSourceLabel(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "soundcloud" => "SoundCloud",
            "tidal" => "Tidal",
            "qobuz" => "Qobuz",
            "bandcamp" => "Bandcamp",
            "pandora" => "Pandora",
            _ => "External"
        };
    }
}
