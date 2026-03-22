using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/tagging/profiles")]
[Authorize]
public sealed class TaggingProfilesApiController : ControllerBase
{
    private const string DownloadTagSourceKey = "downloadTagSource";
    private const string SpotifySource = "spotify";
    private const string DeezerSource = "deezer";

    private readonly TaggingProfileService _profiles;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    public TaggingProfilesApiController(
        TaggingProfileService profiles,
        AutoTagProfileResolutionService profileResolutionService)
    {
        _profiles = profiles;
        _profileResolutionService = profileResolutionService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _profiles.LoadAsync();
        return Ok(list
            .OrderBy(p => p.Name)
            .Select(ToResponseModel));
    }

    public sealed record TaggingProfileUpsertRequest(
        string? Id,
        string Name,
        bool? IsDefault,
        Dictionary<string, string>? TagConfig,
        AutoTagSettings? AutoTag,
        TechnicalTagSettings? Technical,
        FolderStructureSettings? FolderStructure,
        VerificationSettings? Verification);

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] TaggingProfileUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Profile name is required.");
        }

        var existing = await _profiles.GetByIdAsync(request.Id);
        var tagConfig = TryBuildTagConfig(request, out _, out _)
            ?? ConvertTagConfig(request.TagConfig);

        // Preserve existing tag config if the caller did not send tag sources.
        if (request.TagConfig is null && request.AutoTag is null && existing is not null)
        {
            tagConfig = existing.TagConfig;
        }

        var profile = new TaggingProfile
        {
            Id = request.Id ?? string.Empty,
            Name = request.Name,
            IsDefault = request.IsDefault ?? existing?.IsDefault ?? false,
            TagConfig = tagConfig,
            AutoTag = SanitizeAutoTagSettings(request.AutoTag ?? existing?.AutoTag ?? new AutoTagSettings()),
            Technical = request.Technical ?? existing?.Technical ?? new TechnicalTagSettings(),
            FolderStructure = request.FolderStructure ?? existing?.FolderStructure ?? new FolderStructureSettings(),
            Verification = request.Verification ?? existing?.Verification ?? new VerificationSettings()
        };

        var saved = await _profiles.UpsertAsync(profile);
        if (saved is null)
        {
            return BadRequest("Profile name is required.");
        }

        return Ok(ToResponseModel(saved));
    }

    public sealed record CopyProfileRequest(string Name);

    [HttpPost("{id}/copy")]
    public async Task<IActionResult> Copy(string id, [FromBody] CopyProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Copied profile name is required.");
        }

        var source = await _profiles.GetByIdAsync(id);
        if (source == null)
        {
            return NotFound(new { error = "Profile not found." });
        }

        var normalizedName = request.Name.Trim();
        var profiles = await _profiles.LoadAsync();
        var nameExists = profiles.Any(profile =>
            string.Equals(profile.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));
        if (nameExists)
        {
            return Conflict(new { error = $"A profile named '{normalizedName}' already exists." });
        }

        var copy = DeepCloneProfile(source);
        copy.Id = string.Empty;
        copy.Name = normalizedName;
        copy.IsDefault = false;

        var saved = await _profiles.UpsertAsync(copy);
        if (saved == null)
        {
            return BadRequest("Copied profile name is required.");
        }

        return Ok(ToResponseModel(saved));
    }

    private static object ToResponseModel(TaggingProfile profile)
    {
        var sanitizedAutoTag = SanitizeAutoTagSettings(profile.AutoTag);
        return new
        {
            profile.Id,
            profile.Name,
            profile.IsDefault,
            profile.TagConfig,
            AutoTag = BuildAutoTagResponse(sanitizedAutoTag),
            profile.Technical,
            profile.FolderStructure,
            profile.Verification
        };
    }

    private static Dictionary<string, object?> BuildAutoTagResponse(AutoTagSettings? autoTag)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (autoTag?.Data is { Count: > 0 })
        {
            foreach (var entry in autoTag.Data)
            {
                data[entry.Key] = entry.Value;
            }
        }

        var response = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in data)
        {
            response[entry.Key] = entry.Value;
        }

        // Backward-compat: expose nested shape as well as flat shape.
        response["data"] = data;
        return response;
    }

    private static AutoTagSettings SanitizeAutoTagSettings(AutoTagSettings? autoTag)
    {
        var sanitized = autoTag ?? new AutoTagSettings();
        sanitized.Data ??= new Dictionary<string, JsonElement>();
        StripAuthSecrets(sanitized.Data);
        EnsureBooleanAutoTagDefault(sanitized.Data, "overwrite", false);
        EnsureStringArrayAutoTagDefault(sanitized.Data, "overwriteTags");
        EnsureDownloadTagSourceDefault(sanitized.Data);
        return sanitized;
    }

    private static void EnsureBooleanAutoTagDefault(Dictionary<string, JsonElement> data, string key, bool defaultValue)
    {
        var existingKey = data.Keys.FirstOrDefault(entry =>
            string.Equals(entry, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(existingKey))
        {
            data[key] = JsonSerializer.SerializeToElement(defaultValue);
            return;
        }

        var value = data[existingKey];
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            data[existingKey] = JsonSerializer.SerializeToElement(defaultValue);
        }
    }

    private static void EnsureStringArrayAutoTagDefault(Dictionary<string, JsonElement> data, string key)
    {
        var existingKey = data.Keys.FirstOrDefault(entry =>
            string.Equals(entry, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(existingKey))
        {
            data[key] = JsonSerializer.SerializeToElement(Array.Empty<string>());
            return;
        }

        var value = data[existingKey];
        if (value.ValueKind != JsonValueKind.Array)
        {
            data[existingKey] = JsonSerializer.SerializeToElement(Array.Empty<string>());
            return;
        }

        var normalized = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        data[existingKey] = JsonSerializer.SerializeToElement(normalized);
    }

    private static void EnsureDownloadTagSourceDefault(Dictionary<string, JsonElement> data)
    {
        var key = data.Keys.FirstOrDefault(entry =>
            string.Equals(entry, DownloadTagSourceKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(key))
        {
            data[DownloadTagSourceKey] = JsonSerializer.SerializeToElement(DeezerSource);
            return;
        }

        var value = data[key];
        if (value.ValueKind != JsonValueKind.String)
        {
            data[key] = JsonSerializer.SerializeToElement(DeezerSource);
            return;
        }

        var normalized = NormalizeDownloadTagSource(value.GetString());
        data[key] = JsonSerializer.SerializeToElement(normalized);
    }

    private static void StripAuthSecrets(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("custom", out var customElement) || customElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        try
        {
            var customNode = JsonNode.Parse(customElement.GetRawText()) as JsonObject;
            if (customNode == null)
            {
                return;
            }

            var changed = false;
            changed |= RemoveCustomField(customNode, "discogs", "token");
            changed |= RemoveCustomField(customNode, "lastfm", "apiKey");
            changed |= RemoveCustomField(customNode, "bpmsupreme", "email");
            changed |= RemoveCustomField(customNode, "bpmsupreme", "password");

            if (!changed)
            {
                return;
            }

            data["custom"] = JsonSerializer.SerializeToElement(customNode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Best-effort: keep profile save resilient even if custom payload is malformed.
        }
    }

    private static bool RemoveCustomField(JsonObject customNode, string platformId, string field)
    {
        if (customNode[platformId] is not JsonObject platformNode)
        {
            return false;
        }

        return platformNode.Remove(field);
    }

    private static string NormalizeDownloadTagSource(string? downloadTagSource)
    {
        return downloadTagSource?.Trim().ToLowerInvariant() switch
        {
            SpotifySource => SpotifySource,
            DeezerSource => DeezerSource,
            _ => DeezerSource
        };
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var existing = await _profiles.GetByIdAsync(id);
        var removed = await _profiles.DeleteAsync(id);
        if (removed)
        {
            await _profileResolutionService.RemoveDeletedProfileReferencesAsync(id, existing?.Name, cancellationToken);
        }

        return Ok(new { removed });
    }

    private static TaggingProfile DeepCloneProfile(TaggingProfile profile)
        => JsonSerializer.Deserialize<TaggingProfile>(JsonSerializer.Serialize(profile))
            ?? new TaggingProfile();

    private static UnifiedTagConfig ConvertTagConfig(Dictionary<string, string>? input)
    {
        if (input is null || input.Count == 0)
        {
            return new UnifiedTagConfig();
        }

        TagSource Parse(string key)
        {
            if (!input.TryGetValue(key, out var value))
            {
                return TagSource.DownloadSource;
            }

            return value.ToLowerInvariant() switch
            {
                "download" => TagSource.DownloadSource,
                "autotag" => TagSource.AutoTagPlatform,
                "both" => TagSource.Both,
                "none" => TagSource.None,
                _ => TagSource.DownloadSource
            };
        }

        return new UnifiedTagConfig
        {
            Title = Parse("title"),
            Artist = Parse("artist"),
            Artists = Parse("artists"),
            Album = Parse("album"),
            AlbumArtist = Parse("albumArtist"),
            Cover = Parse("cover"),
            TrackNumber = Parse("trackNumber"),
            TrackTotal = Parse("trackTotal"),
            DiscNumber = Parse("discNumber"),
            DiscTotal = Parse("discTotal"),
            Genre = Parse("genre"),
            Year = Parse("year"),
            Date = Parse("date"),
            Isrc = Parse("isrc"),
            Barcode = Parse("barcode"),
            Bpm = Parse("bpm"),
            Duration = Parse("duration"),
            ReplayGain = Parse("replayGain"),
            Danceability = Parse("danceability"),
            Energy = Parse("energy"),
            Valence = Parse("valence"),
            Acousticness = Parse("acousticness"),
            Instrumentalness = Parse("instrumentalness"),
            Speechiness = Parse("speechiness"),
            Loudness = Parse("loudness"),
            Tempo = Parse("tempo"),
            TimeSignature = Parse("timeSignature"),
            Liveness = Parse("liveness"),
            Label = Parse("label"),
            Copyright = Parse("copyright"),
            UnsyncedLyrics = Parse("unsyncedLyrics"),
            SyncedLyrics = Parse("syncedLyrics"),
            Composer = Parse("composer"),
            InvolvedPeople = Parse("involvedPeople"),
            Source = Parse("source"),
            Explicit = Parse("explicit"),
            Rating = Parse("rating"),
            Style = Parse("style"),
            ReleaseDate = Parse("releaseDate"),
            PublishDate = Parse("publishDate"),
            ReleaseId = Parse("releaseId"),
            TrackId = Parse("trackId"),
            CatalogNumber = Parse("catalogNumber"),
            Key = Parse("key"),
            Remixer = Parse("remixer"),
            Version = Parse("version"),
            Mood = Parse("mood"),
            Url = Parse("url"),
            OtherTags = Parse("otherTags"),
            MetaTags = Parse("metaTags")
        };
    }

    private static UnifiedTagConfig? TryBuildTagConfig(
        TaggingProfileUpsertRequest request,
        out bool derived,
        out bool hasDownloadTags)
    {
        derived = false;
        hasDownloadTags = false;
        if (request.TagConfig is { Count: > 0 })
        {
            return null;
        }

        if (request.AutoTag?.Data == null || request.AutoTag.Data.Count == 0)
        {
            return null;
        }

        var hasTags = TryGetTagList(request.AutoTag.Data, "tags", out var enrichTags);
        if (!TryGetTagList(request.AutoTag.Data, "downloadTags", out var downloadTags)
            && !hasTags)
        {
            return null;
        }

        var config = CreateEmptyUnifiedTagConfig();
        var downloadTagMode = TagSource.DownloadSource;

        if (downloadTags.Count > 0)
        {
            derived = true;
            hasDownloadTags = true;
            foreach (var tag in downloadTags)
            {
                ApplyTagSource(config, tag, downloadTagMode);
            }
        }

        if (hasTags && enrichTags.Count > 0)
        {
            derived = true;
            foreach (var tag in enrichTags)
            {
                ApplyTagSource(config, tag, TagSource.AutoTagPlatform);
            }
        }

        return config;
    }

    private static bool TryGetTagList(Dictionary<string, JsonElement> data, string key, out List<string> tags)
    {
        tags = new List<string>();
        if (!data.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var value in element.EnumerateArray()
                     .Where(static item => item.ValueKind == JsonValueKind.String)
                     .Select(static item => item.GetString())
                     .Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            tags.Add(value!);
        }

        return tags.Count > 0;
    }

    private static UnifiedTagConfig CreateEmptyUnifiedTagConfig()
    {
        return new UnifiedTagConfig
        {
            Title = TagSource.None,
            Artist = TagSource.None,
            Artists = TagSource.None,
            Album = TagSource.None,
            AlbumArtist = TagSource.None,
            Cover = TagSource.None,
            TrackNumber = TagSource.None,
            TrackTotal = TagSource.None,
            DiscNumber = TagSource.None,
            DiscTotal = TagSource.None,
            Genre = TagSource.None,
            Year = TagSource.None,
            Date = TagSource.None,
            Isrc = TagSource.None,
            Barcode = TagSource.None,
            Bpm = TagSource.None,
            Duration = TagSource.None,
            ReplayGain = TagSource.None,
            Danceability = TagSource.None,
            Energy = TagSource.None,
            Valence = TagSource.None,
            Acousticness = TagSource.None,
            Instrumentalness = TagSource.None,
            Speechiness = TagSource.None,
            Loudness = TagSource.None,
            Tempo = TagSource.None,
            TimeSignature = TagSource.None,
            Liveness = TagSource.None,
            Label = TagSource.None,
            Copyright = TagSource.None,
            UnsyncedLyrics = TagSource.None,
            SyncedLyrics = TagSource.None,
            Composer = TagSource.None,
            InvolvedPeople = TagSource.None,
            Source = TagSource.None,
            Explicit = TagSource.None,
            Rating = TagSource.None,
            Style = TagSource.None,
            ReleaseDate = TagSource.None,
            PublishDate = TagSource.None,
            ReleaseId = TagSource.None,
            TrackId = TagSource.None,
            CatalogNumber = TagSource.None,
            Key = TagSource.None,
            Remixer = TagSource.None,
            Version = TagSource.None,
            Mood = TagSource.None,
            Url = TagSource.None,
            OtherTags = TagSource.None,
            MetaTags = TagSource.None
        };
    }

    private static TagSource MergeTagSource(TagSource current, TagSource next)
    {
        if (current == TagSource.None)
        {
            return next;
        }
        if (next == TagSource.None || current == next)
        {
            return current;
        }
        return TagSource.Both;
    }

    private static void ApplyTagSource(UnifiedTagConfig config, string tag, TagSource source)
    {
        var key = tag?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        switch (key)
        {
            case "title":
                config.Title = MergeTagSource(config.Title, source);
                break;
            case "artist":
                config.Artist = MergeTagSource(config.Artist, source);
                break;
            case "artists":
                config.Artists = MergeTagSource(config.Artists, source);
                break;
            case "album":
                config.Album = MergeTagSource(config.Album, source);
                break;
            case "albumArtist":
                config.AlbumArtist = MergeTagSource(config.AlbumArtist, source);
                break;
            case "cover":
            case "albumArt":
                config.Cover = MergeTagSource(config.Cover, source);
                break;
            case "trackNumber":
                config.TrackNumber = MergeTagSource(config.TrackNumber, source);
                break;
            case "trackTotal":
                config.TrackTotal = MergeTagSource(config.TrackTotal, source);
                break;
            case "discNumber":
                config.DiscNumber = MergeTagSource(config.DiscNumber, source);
                break;
            case "discTotal":
                config.DiscTotal = MergeTagSource(config.DiscTotal, source);
                break;
            case "genre":
                config.Genre = MergeTagSource(config.Genre, source);
                break;
            case "year":
                config.Year = MergeTagSource(config.Year, source);
                break;
            case "date":
                config.Date = MergeTagSource(config.Date, source);
                break;
            case "isrc":
                config.Isrc = MergeTagSource(config.Isrc, source);
                break;
            case "barcode":
            case "upc":
                config.Barcode = MergeTagSource(config.Barcode, source);
                break;
            case "bpm":
                config.Bpm = MergeTagSource(config.Bpm, source);
                break;
            case "duration":
            case "length":
                config.Duration = MergeTagSource(config.Duration, source);
                break;
            case "replayGain":
                config.ReplayGain = MergeTagSource(config.ReplayGain, source);
                break;
            case "danceability":
                config.Danceability = MergeTagSource(config.Danceability, source);
                break;
            case "energy":
                config.Energy = MergeTagSource(config.Energy, source);
                break;
            case "valence":
                config.Valence = MergeTagSource(config.Valence, source);
                break;
            case "acousticness":
                config.Acousticness = MergeTagSource(config.Acousticness, source);
                break;
            case "instrumentalness":
                config.Instrumentalness = MergeTagSource(config.Instrumentalness, source);
                break;
            case "speechiness":
                config.Speechiness = MergeTagSource(config.Speechiness, source);
                break;
            case "loudness":
                config.Loudness = MergeTagSource(config.Loudness, source);
                break;
            case "tempo":
                config.Tempo = MergeTagSource(config.Tempo, source);
                break;
            case "timeSignature":
                config.TimeSignature = MergeTagSource(config.TimeSignature, source);
                break;
            case "liveness":
                config.Liveness = MergeTagSource(config.Liveness, source);
                break;
            case "label":
                config.Label = MergeTagSource(config.Label, source);
                break;
            case "copyright":
                config.Copyright = MergeTagSource(config.Copyright, source);
                break;
            case "unsyncedLyrics":
            case "lyrics":
                config.UnsyncedLyrics = MergeTagSource(config.UnsyncedLyrics, source);
                break;
            case "syncedLyrics":
                config.SyncedLyrics = MergeTagSource(config.SyncedLyrics, source);
                break;
            case "composer":
                config.Composer = MergeTagSource(config.Composer, source);
                break;
            case "involvedPeople":
                config.InvolvedPeople = MergeTagSource(config.InvolvedPeople, source);
                break;
            case "source":
                config.Source = MergeTagSource(config.Source, source);
                break;
            case "explicit":
                config.Explicit = MergeTagSource(config.Explicit, source);
                break;
            case "rating":
                config.Rating = MergeTagSource(config.Rating, source);
                break;
            case "style":
                config.Style = MergeTagSource(config.Style, source);
                break;
            case "releaseDate":
                config.ReleaseDate = MergeTagSource(config.ReleaseDate, source);
                break;
            case "publishDate":
                config.PublishDate = MergeTagSource(config.PublishDate, source);
                break;
            case "releaseId":
                config.ReleaseId = MergeTagSource(config.ReleaseId, source);
                break;
            case "trackId":
                config.TrackId = MergeTagSource(config.TrackId, source);
                break;
            case "catalogNumber":
                config.CatalogNumber = MergeTagSource(config.CatalogNumber, source);
                break;
            case "key":
                config.Key = MergeTagSource(config.Key, source);
                break;
            case "remixer":
                config.Remixer = MergeTagSource(config.Remixer, source);
                break;
            case "version":
                config.Version = MergeTagSource(config.Version, source);
                break;
            case "mood":
                config.Mood = MergeTagSource(config.Mood, source);
                break;
            case "url":
                config.Url = MergeTagSource(config.Url, source);
                break;
            case "otherTags":
                config.OtherTags = MergeTagSource(config.OtherTags, source);
                break;
            case "metaTags":
                config.MetaTags = MergeTagSource(config.MetaTags, source);
                break;
        }
    }
}
