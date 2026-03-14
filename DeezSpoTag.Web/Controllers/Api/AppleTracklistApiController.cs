using System.Text.Json;
using System.Linq;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/apple/tracklist")]
[Authorize]
public sealed class AppleTracklistApiController : ControllerBase
{
    private static readonly bool AppleDisabled = ReadAppleDisabled();
    private const string AppleSource = "apple";
    private const string AlbumType = "album";
    private const string TrackType = "track";
    private const string PlaylistType = "playlist";
    private const string DefaultLanguage = "en-US";
    private const string DataField = "data";
    private const string RelationshipsField = "relationships";
    private const string TracksField = "tracks";
    private const string NameField = "name";
    private const string UrlField = "url";
    private const string ArtworkField = "artwork";
    private const string PreviewsField = "previews";
    private const string ReleaseDateField = "releaseDate";
    private const string AlbumNameField = "albumName";
    private const string HasLyricsField = "hasLyrics";
    private const string HasTimeSyncedLyricsField = "hasTimeSyncedLyrics";
    private const string AttributesField = "attributes";
    private const string ArtistNameField = "artistName";
    private const string AudioTraitsField = "audioTraits";
    private readonly AppleMusicCatalogService _catalog;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<AppleTracklistApiController> _logger;
    public AppleTracklistApiController(
        AppleMusicCatalogService catalog,
        DeezSpoTagSettingsService settingsService,
        ILogger<AppleTracklistApiController> logger)
    {
        _catalog = catalog;
        _settingsService = settingsService;
        _logger = logger;
    }

    private static bool ReadAppleDisabled()
    {
        var value = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_DISABLED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string id, [FromQuery] string type = AlbumType, [FromQuery] string? appleUrl = null, CancellationToken cancellationToken = default)
    {
        if (AppleDisabled)
        {
            return StatusCode(503, new { error = "Apple Music is disabled." });
        }

        var resolvedId = ResolveAppleId(id, appleUrl);

        if (string.IsNullOrWhiteSpace(resolvedId))
        {
            return BadRequest("id is required");
        }

        type = (type ?? AlbumType).ToLowerInvariant();

        try
        {
            var storefront = await ResolveStorefrontAsync(cancellationToken);
            if (type == AlbumType)
            {
                var payload = await GetAlbumTracklist(resolvedId, storefront, cancellationToken);
                return Ok(payload);
            }

            if (type == TrackType)
            {
                var payload = await GetSingleTrack(resolvedId, storefront, cancellationToken);
                return Ok(payload);
            }

            if (type == PlaylistType)
            {
                var payload = await GetPlaylistTracklist(resolvedId, storefront, cancellationToken);
                return Ok(payload);
            }

            return BadRequest($"Unsupported type '{type}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple tracklist fetch failed for Id (Type)");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string ResolveAppleId(string id, string? appleUrl)
        => AppleIdParser.Resolve(id, appleUrl) ?? string.Empty;

    private async Task<string> ResolveStorefrontAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            return await _catalog.ResolveStorefrontAsync(
                settings.AppleMusic?.Storefront,
                settings.AppleMusic?.MediaUserToken,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple tracklist storefront resolution failed; falling back to us.");
            return "us";
        }
    }

    private async Task<object> GetAlbumTracklist(string id, string storefront, CancellationToken cancellationToken)
    {
        using var doc = await _catalog.GetAlbumAsync(id, storefront, language: DefaultLanguage, cancellationToken);
        var root = doc.RootElement;
        if (!root.TryGetProperty(DataField, out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Album not found");
        }

        var album = dataArr[0];
        var attrs = album.GetProperty(AttributesField);
        var rel = album.GetProperty(RelationshipsField);
        var tracks = BuildRelationshipTracks(rel);

        var cover = ResolveArtwork(attrs);
        var title = attrs.TryGetProperty(NameField, out var nameEl) ? nameEl.GetString() ?? "" : "";
        var artistName = attrs.TryGetProperty(ArtistNameField, out var artistEl) ? artistEl.GetString() ?? "" : "";
        var trackCount = attrs.TryGetProperty("trackCount", out var tcEl) ? tcEl.GetInt32() : tracks.Count;
        var releaseDate = attrs.TryGetProperty(ReleaseDateField, out var rdEl) ? rdEl.GetString() ?? "" : "";

        return BuildTracklistResponse(title, artistName, cover, trackCount, releaseDate, tracks);
    }

    private async Task<object> GetSingleTrack(string id, string storefront, CancellationToken cancellationToken)
    {
        using var doc = await _catalog.GetSongAsync(id, storefront, language: DefaultLanguage, cancellationToken);
        var root = doc.RootElement;
        if (!root.TryGetProperty(DataField, out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Track not found");
        }

        var track = dataArr[0];
        var ta = track.GetProperty(AttributesField);

        var cover = ResolveArtwork(ta);
        var title = ta.TryGetProperty(NameField, out var nameEl) ? nameEl.GetString() ?? "" : "";
        var artistName = ta.TryGetProperty(ArtistNameField, out var artistEl) ? artistEl.GetString() ?? "" : "";
        var releaseDate = ta.TryGetProperty(ReleaseDateField, out var rdEl) ? rdEl.GetString() ?? "" : "";
        var albumName = ta.TryGetProperty(AlbumNameField, out var an) ? an.GetString() ?? "" : "";

        var tracks = new List<object> { BuildTrackEntry(track, ta, title, artistName, albumName) };
        return BuildTracklistResponse(title, artistName, cover, 1, releaseDate, tracks);
    }

    private async Task<object> GetPlaylistTracklist(string id, string storefront, CancellationToken cancellationToken)
    {
        using var doc = await _catalog.GetPlaylistAsync(id, storefront, language: DefaultLanguage, cancellationToken);
        var root = doc.RootElement;
        if (!root.TryGetProperty(DataField, out var dataArr) || dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Playlist not found");
        }

        var playlist = dataArr[0];
        var attrs = playlist.GetProperty(AttributesField);
        var rel = playlist.GetProperty(RelationshipsField);
        var tracks = BuildRelationshipTracks(rel);

        var cover = ResolveArtwork(attrs);
        var title = attrs.TryGetProperty(NameField, out var nameEl) ? nameEl.GetString() ?? "" : "";
        var curator = attrs.TryGetProperty("curatorName", out var curatorEl) ? curatorEl.GetString() ?? "" : "";
        var trackCount = attrs.TryGetProperty("trackCount", out var tcEl) ? tcEl.GetInt32() : tracks.Count;

        return BuildTracklistResponse(title, curator, cover, trackCount, string.Empty, tracks);
    }

    private static List<object> BuildRelationshipTracks(JsonElement relationships)
    {
        var tracks = new List<object>();
        if (!relationships.TryGetProperty(TracksField, out var tracksRel)
            || tracksRel.ValueKind != JsonValueKind.Object
            || !tracksRel.TryGetProperty(DataField, out var tracksData)
            || tracksData.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        foreach (var track in tracksData.EnumerateArray())
        {
            if (!track.TryGetProperty(AttributesField, out var trackAttributes)
                || trackAttributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = trackAttributes.TryGetProperty(NameField, out var titleElement) ? titleElement.GetString() ?? "" : "";
            var artistName = trackAttributes.TryGetProperty(ArtistNameField, out var artistElement) ? artistElement.GetString() ?? "" : "";
            var albumName = trackAttributes.TryGetProperty(AlbumNameField, out var albumElement) ? albumElement.GetString() ?? "" : "";
            tracks.Add(BuildTrackEntry(track, trackAttributes, title, artistName, albumName));
        }

        return tracks;
    }

    private static object BuildTrackEntry(JsonElement track, JsonElement attributes, string title, string artistName, string albumName)
    {
        return new
        {
            id = track.GetProperty("id").GetString() ?? "",
            title,
            isrc = attributes.TryGetProperty("isrc", out var isrcEl) ? isrcEl.GetString() ?? "" : "",
            duration = attributes.TryGetProperty("durationInMillis", out var d) ? (d.GetInt32() / 1000) : 0,
            artist = new { name = artistName },
            album = new
            {
                title = albumName,
                cover_medium = ResolveArtwork(attributes)
            },
            source = AppleSource,
            sourceUrl = attributes.TryGetProperty(UrlField, out var u) ? u.GetString() ?? "" : "",
            preview = ResolvePreviewUrl(attributes),
            hasAtmos = HasAtmos(attributes),
            hasLyrics = ReadOptionalBoolAttribute(attributes, HasLyricsField),
            hasTimeSyncedLyrics = ReadOptionalBoolAttribute(attributes, HasTimeSyncedLyricsField),
            audioTraits = ReadAudioTraits(attributes),
            hasAppleDigitalMaster = HasAppleDigitalMaster(attributes),
            bitDepth = ResolveBitDepth(attributes),
            sampleRate = ResolveSampleRate(attributes)
        };
    }

    private static object BuildTracklistResponse(
        string title,
        string artistName,
        string cover,
        int trackCount,
        string releaseDate,
        List<object> tracks)
    {
        return new
        {
            tracklist = new
            {
                title,
                artist = new { name = artistName },
                cover_big = cover,
                nb_tracks = trackCount,
                release_date = releaseDate,
                tracks
            }
        };
    }

    private static string ResolveArtwork(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (!attributes.TryGetProperty(ArtworkField, out var art) || art.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (!art.TryGetProperty(UrlField, out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        var raw = urlEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var width = art.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = art.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        return AppleArtworkRenderHelper.BuildArtworkUrl(raw, width, height);
    }

    private static bool HasAtmos(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return attributes.TryGetProperty(AudioTraitsField, out var traits)
            && traits.ValueKind == JsonValueKind.Array
            && traits.EnumerateArray()
                .Where(static trait => trait.ValueKind == JsonValueKind.String)
                .Select(static trait => trait.GetString())
                .Any(static value => value?.IndexOf("atmos", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static IReadOnlyList<string> ReadAudioTraits(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        if (!attributes.TryGetProperty(AudioTraitsField, out var traits) || traits.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var trait in traits.EnumerateArray())
        {
            if (trait.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = trait.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool HasAppleDigitalMaster(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (attributes.TryGetProperty("isAppleDigitalMaster", out var admEl) && admEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return admEl.GetBoolean();
        }

        if (attributes.TryGetProperty("isMasteredForItunes", out var mfiEl) && mfiEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return mfiEl.GetBoolean();
        }

        return false;
    }

    private static string ResolveBitDepth(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!attributes.TryGetProperty(AudioTraitsField, out var traits) || traits.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return traits.EnumerateArray()
            .Where(static trait => trait.ValueKind == JsonValueKind.String)
            .Select(static trait => trait.GetString() ?? string.Empty)
            .Where(static value => value.StartsWith("bit-", StringComparison.OrdinalIgnoreCase))
            .Select(static value => value.Replace("bit-", "", StringComparison.OrdinalIgnoreCase).Trim() + "B")
            .FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveSampleRate(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!attributes.TryGetProperty(AudioTraitsField, out var traits) || traits.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return traits.EnumerateArray()
            .Where(static trait => trait.ValueKind == JsonValueKind.String)
            .Select(static trait => trait.GetString() ?? string.Empty)
            .Where(static value => value.EndsWith("khz", StringComparison.OrdinalIgnoreCase))
            .Select(static value => value.ToUpperInvariant().Replace("KHZ", "kHz"))
            .FirstOrDefault() ?? string.Empty;
    }

    private static bool ReadOptionalBoolAttribute(JsonElement attributes, string propertyName)
    {
        return attributes.TryGetProperty(propertyName, out var boolEl)
            && boolEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            && boolEl.GetBoolean();
    }

    private static string ResolvePreviewUrl(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (attributes.TryGetProperty(PreviewsField, out var previews) && previews.ValueKind == JsonValueKind.Array)
        {
            foreach (var preview in previews.EnumerateArray())
            {
                if (preview.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (preview.TryGetProperty(UrlField, out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    return urlEl.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }
}
