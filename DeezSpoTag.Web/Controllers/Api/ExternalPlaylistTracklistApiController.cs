using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.LinkMapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/external/playlist/tracklist")]
[Authorize]
public sealed class ExternalPlaylistTracklistApiController : ControllerBase
{
    private const string SoundCloudSource = "soundcloud";
    private const string TidalSource = "tidal";
    private const string QobuzSource = "qobuz";
    private const string BandcampSource = "bandcamp";
    private const string PandoraSource = "pandora";
    private static readonly HashSet<string> SupportedSources = new(StringComparer.OrdinalIgnoreCase)
    {
        SoundCloudSource,
        TidalSource,
        QobuzSource,
        BandcampSource,
        PandoraSource
    };

    private readonly ILogger<ExternalPlaylistTracklistApiController> _logger;

    public ExternalPlaylistTracklistApiController(ILogger<ExternalPlaylistTracklistApiController> logger)
    {
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
        return await YtDlpPlaylistJsonFetcher.FetchAsync(playlistUrl, "external", _logger, cancellationToken);
    }

    private static bool TryBuildTracklistPayload(string source, string sourceUrl, string rawJson, out object tracklist)
    {
        tracklist = new { };
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (!TryBuildPlaylistMetadata(source, sourceUrl, root, out var metadata))
        {
            return false;
        }

        var tracks = BuildTrackEntries(source, root, metadata);
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

    private static bool TryBuildPlaylistMetadata(
        string source,
        string sourceUrl,
        JsonElement root,
        out PlaylistMetadata metadata)
    {
        metadata = new PlaylistMetadata(
            GetString(root, "id"),
            GetString(root, "title"),
            GetPrimaryMetadataValue(
                GetString(root, "uploader"),
                GetString(root, "artist"),
                GetString(root, "channel"),
                GetString(root, "creator"),
                FormatSourceLabel(source)),
            GetString(root, "description"),
            ExtractCoverUrl(root));

        if (string.IsNullOrWhiteSpace(metadata.PlaylistId))
        {
            metadata = metadata with { PlaylistId = BuildStableId(sourceUrl) };
        }

        if (string.IsNullOrWhiteSpace(metadata.PlaylistTitle))
        {
            metadata = metadata with { PlaylistTitle = $"{FormatSourceLabel(source)} Playlist" };
        }

        return !string.IsNullOrWhiteSpace(metadata.PlaylistId)
               && !string.IsNullOrWhiteSpace(metadata.PlaylistTitle);
    }

    private static List<object> BuildTrackEntries(string source, JsonElement root, PlaylistMetadata metadata)
    {
        var tracks = new List<object>();
        if (!root.TryGetProperty("entries", out var entriesElement)
            || entriesElement.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        var position = 0;
        foreach (var entry in entriesElement.EnumerateArray())
        {
            var entryUrl = ResolveEntryUrl(source, entry);
            if (string.IsNullOrWhiteSpace(entryUrl))
            {
                continue;
            }

            position++;
            tracks.Add(BuildTrackEntry(entry, entryUrl, position, metadata));
        }

        return tracks;
    }

    private static object BuildTrackEntry(JsonElement entry, string entryUrl, int position, PlaylistMetadata metadata)
    {
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

        var artistName = GetPrimaryMetadataValue(
            GetString(entry, "artist"),
            GetString(entry, "uploader"),
            GetString(entry, "channel"),
            GetString(entry, "creator"),
            metadata.CreatorName);

        return new
        {
            id = entryId,
            title,
            duration = GetInt(entry, "duration"),
            track_position = position,
            link = entryUrl,
            sourceUrl = entryUrl,
            artist = new { id = string.Empty, name = artistName },
            album = new { id = metadata.PlaylistId, title = metadata.PlaylistTitle, cover_medium = metadata.CoverUrl }
        };
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

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
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

    private static string NormalizeSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            SoundCloudSource => SoundCloudSource,
            TidalSource => TidalSource,
            QobuzSource => QobuzSource,
            BandcampSource => BandcampSource,
            PandoraSource => PandoraSource,
            _ => string.Empty
        };
    }

    private static string ClassifySource(string normalizedUrl)
    {
        var classified = ExternalLinkClassifier.Classify(normalizedUrl);
        return classified switch
        {
            ExternalLinkSource.SoundCloud => SoundCloudSource,
            ExternalLinkSource.Tidal => TidalSource,
            ExternalLinkSource.Qobuz => QobuzSource,
            ExternalLinkSource.Bandcamp => BandcampSource,
            ExternalLinkSource.Pandora => PandoraSource,
            _ => string.Empty
        };
    }

    private static bool IsPlaylistLikeUrl(Uri uri, string source)
    {
        var path = (uri.AbsolutePath ?? string.Empty).ToLowerInvariant();
        return source.ToLowerInvariant() switch
        {
            SoundCloudSource => path.Contains("/sets/", StringComparison.Ordinal),
            TidalSource => path.Contains("/playlist/", StringComparison.Ordinal) || path.Contains("/mix/", StringComparison.Ordinal),
            QobuzSource => path.Contains("/playlist/", StringComparison.Ordinal),
            BandcampSource => path.Contains("/album/", StringComparison.Ordinal),
            PandoraSource => path.Contains("/playlist/", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string GetPrimaryMetadataValue(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FormatSourceLabel(string source)
    {
        return source.ToLowerInvariant() switch
        {
            SoundCloudSource => "SoundCloud",
            TidalSource => "Tidal",
            QobuzSource => "Qobuz",
            BandcampSource => "Bandcamp",
            PandoraSource => "Pandora",
            _ => "External"
        };
    }

    private sealed record PlaylistMetadata(
        string PlaylistId,
        string PlaylistTitle,
        string CreatorName,
        string Description,
        string CoverUrl);
}
