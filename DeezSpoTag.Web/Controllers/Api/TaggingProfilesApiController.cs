using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/tagging/profiles")]
[Authorize]
public sealed class TaggingProfilesApiController : ControllerBase
{
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
        JsonElement? TagConfig,
        AutoTagSettings? AutoTag,
        TechnicalTagSettings? Technical,
        FolderStructureSettings? FolderStructure,
        VerificationSettings? Verification,
        bool? ApplyToRuntime);

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] TaggingProfileUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Profile name is required.");
        }

        var existing = await _profiles.GetByIdAsync(request.Id);
        var tagConfig = TryBuildTagConfig(request, out _, out _)
            ?? (HasTagConfigPayload(request.TagConfig)
                ? ConvertTagConfig(request.TagConfig)
                : existing?.TagConfig ?? new UnifiedTagConfig());

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
        return TaggingProfileDataHelper.SanitizeAutoTagSettings(autoTag);
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

    private static UnifiedTagConfig ConvertTagConfig(JsonElement? input)
    {
        if (!HasTagConfigPayload(input))
        {
            return new UnifiedTagConfig();
        }

        var tagConfigObject = input!.Value;
        if (tagConfigObject.ValueKind != JsonValueKind.Object)
        {
            return new UnifiedTagConfig();
        }

        return new UnifiedTagConfig
        {
            Title = ParseTagSource(tagConfigObject, "title"),
            Artist = ParseTagSource(tagConfigObject, "artist"),
            Artists = ParseTagSource(tagConfigObject, "artists"),
            Album = ParseTagSource(tagConfigObject, "album"),
            AlbumArtist = ParseTagSource(tagConfigObject, "albumArtist"),
            Cover = ParseTagSource(tagConfigObject, "cover"),
            TrackNumber = ParseTagSource(tagConfigObject, "trackNumber"),
            TrackTotal = ParseTagSource(tagConfigObject, "trackTotal"),
            DiscNumber = ParseTagSource(tagConfigObject, "discNumber"),
            DiscTotal = ParseTagSource(tagConfigObject, "discTotal"),
            Genre = ParseTagSource(tagConfigObject, "genre"),
            Year = ParseTagSource(tagConfigObject, "year"),
            Date = ParseTagSource(tagConfigObject, "date"),
            Isrc = ParseTagSource(tagConfigObject, "isrc"),
            Barcode = ParseTagSource(tagConfigObject, "barcode"),
            Bpm = ParseTagSource(tagConfigObject, "bpm"),
            Duration = ParseTagSource(tagConfigObject, "duration"),
            ReplayGain = ParseTagSource(tagConfigObject, "replayGain"),
            Danceability = ParseTagSource(tagConfigObject, "danceability"),
            Energy = ParseTagSource(tagConfigObject, "energy"),
            Valence = ParseTagSource(tagConfigObject, "valence"),
            Acousticness = ParseTagSource(tagConfigObject, "acousticness"),
            Instrumentalness = ParseTagSource(tagConfigObject, "instrumentalness"),
            Speechiness = ParseTagSource(tagConfigObject, "speechiness"),
            Loudness = ParseTagSource(tagConfigObject, "loudness"),
            Tempo = ParseTagSource(tagConfigObject, "tempo"),
            TimeSignature = ParseTagSource(tagConfigObject, "timeSignature"),
            Liveness = ParseTagSource(tagConfigObject, "liveness"),
            Label = ParseTagSource(tagConfigObject, "label"),
            Copyright = ParseTagSource(tagConfigObject, "copyright"),
            UnsyncedLyrics = ParseTagSource(tagConfigObject, "unsyncedLyrics"),
            SyncedLyrics = ParseTagSource(tagConfigObject, "syncedLyrics"),
            Composer = ParseTagSource(tagConfigObject, "composer"),
            InvolvedPeople = ParseTagSource(tagConfigObject, "involvedPeople"),
            Source = ParseTagSource(tagConfigObject, "source"),
            Explicit = ParseTagSource(tagConfigObject, "explicit"),
            Rating = ParseTagSource(tagConfigObject, "rating"),
            Style = ParseTagSource(tagConfigObject, "style"),
            ReleaseDate = ParseTagSource(tagConfigObject, "releaseDate"),
            PublishDate = ParseTagSource(tagConfigObject, "publishDate"),
            ReleaseId = ParseTagSource(tagConfigObject, "releaseId"),
            TrackId = ParseTagSource(tagConfigObject, "trackId"),
            CatalogNumber = ParseTagSource(tagConfigObject, "catalogNumber"),
            Key = ParseTagSource(tagConfigObject, "key"),
            Remixer = ParseTagSource(tagConfigObject, "remixer"),
            Version = ParseTagSource(tagConfigObject, "version"),
            Mood = ParseTagSource(tagConfigObject, "mood"),
            Url = ParseTagSource(tagConfigObject, "url"),
            OtherTags = ParseTagSource(tagConfigObject, "otherTags"),
            MetaTags = ParseTagSource(tagConfigObject, "metaTags")
        };
    }

    private static TagSource ParseTagSource(JsonElement tagConfigObject, string key)
    {
        if (!TryGetPropertyCaseInsensitive(tagConfigObject, key, out var value))
        {
            return TagSource.DownloadSource;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericValue))
        {
            return Enum.IsDefined(typeof(TagSource), numericValue)
                ? (TagSource)numericValue
                : TagSource.DownloadSource;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return TagSource.DownloadSource;
        }

        return ParseTagSourceText(value.GetString());
    }

    private static TagSource ParseTagSourceText(string? value)
    {
        var normalized = value?.Trim();
        if (int.TryParse(normalized, out var enumValue) && Enum.IsDefined(typeof(TagSource), enumValue))
        {
            return (TagSource)enumValue;
        }

        return normalized?.ToLowerInvariant() switch
        {
            "download" => TagSource.DownloadSource,
            "autotag" => TagSource.AutoTagPlatform,
            "both" => TagSource.Both,
            "none" => TagSource.None,
            "downloadsource" => TagSource.DownloadSource,
            "autotagplatform" => TagSource.AutoTagPlatform,
            _ => TagSource.DownloadSource
        };
    }

    private static UnifiedTagConfig? TryBuildTagConfig(
        TaggingProfileUpsertRequest request,
        out bool derived,
        out bool hasDownloadTags)
    {
        derived = false;
        hasDownloadTags = false;
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

        var hasExplicitDownloadTagList = HasExplicitTagArray(request.AutoTag.Data, "downloadTags");
        // Legacy payloads frequently carried only "tags" (AutoTag/enrichment tags) without
        // an explicit "downloadTags" list. In that case we preserve default download-stage
        // tags and only overlay enrichment sources.
        var config = hasExplicitDownloadTagList
            ? CreateEmptyUnifiedTagConfig()
            : new UnifiedTagConfig();
        var downloadTagMode = TagSource.DownloadSource;

        if (hasExplicitDownloadTagList && downloadTags.Count > 0)
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

    private static bool HasExplicitTagArray(Dictionary<string, JsonElement> data, string key)
    {
        var matchingKey = data.Keys.FirstOrDefault(entry =>
            string.Equals(entry, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingKey))
        {
            return false;
        }

        return data[matchingKey].ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetTagList(Dictionary<string, JsonElement> data, string key, out List<string> tags)
    {
        tags = new List<string>();
        var matchingKey = data.Keys.FirstOrDefault(entry =>
            string.Equals(entry, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(matchingKey))
        {
            return false;
        }

        var element = data[matchingKey];
        if (element.ValueKind != JsonValueKind.Array)
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

    private static bool HasTagConfigPayload(JsonElement? tagConfig)
    {
        if (!tagConfig.HasValue)
        {
            return false;
        }

        var value = tagConfig.Value;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return value.EnumerateObject().Any();
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string key, out JsonElement value)
    {
        var property = element.EnumerateObject()
            .FirstOrDefault(property => string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(property.Name))
        {
            value = default;
            return false;
        }

        value = property.Value;
        return true;
    }
}
