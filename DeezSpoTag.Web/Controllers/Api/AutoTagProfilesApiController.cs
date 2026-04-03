using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag/profiles")]
[Authorize]
public class AutoTagProfilesApiController : ControllerBase
{
    private const string DownloadTagSourceKey = "downloadTagSource";
    private const string DeezerSource = "deezer";

    private readonly TaggingProfileService _profiles;
    private readonly AutoTagProfileResolutionService _profileResolutionService;

    public AutoTagProfilesApiController(
        TaggingProfileService profiles,
        AutoTagProfileResolutionService profileResolutionService)
    {
        _profiles = profiles;
        _profileResolutionService = profileResolutionService;
    }

    public sealed record LegacyAutoTagProfileRequest(string Name, JsonElement? Config);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _profiles.LoadAsync();
        return Ok(list
            .OrderBy(p => p.Name)
            .Select(ToLegacyResponse));
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] LegacyAutoTagProfileRequest profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return BadRequest("Profile name is required.");
        }

        var normalizedName = profile.Name.Trim();
        var profiles = await _profiles.LoadAsync();
        var existing = profiles.FirstOrDefault(item =>
            string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

        var target = existing ?? new TaggingProfile
        {
            IsDefault = profiles.Count == 0
        };

        var autoTag = SanitizeAutoTagSettings(ParseAutoTagSettings(profile.Config));
        var tagConfig = BuildTagConfigFromLegacyPayload(autoTag.Data, existing?.TagConfig);

        target.Name = normalizedName;
        target.AutoTag = autoTag;
        target.TagConfig = tagConfig;

        var saved = await _profiles.UpsertAsync(target);
        if (saved == null)
        {
            return BadRequest("Profile name is required.");
        }

        return Ok(ToLegacyResponse(saved));
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        var existing = profiles.FirstOrDefault(item =>
            string.Equals(item.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return Ok(new { removed = false });
        }

        var removed = await _profiles.DeleteAsync(existing.Id);
        if (removed)
        {
            await _profileResolutionService.RemoveDeletedProfileReferencesAsync(existing.Id, existing.Name, cancellationToken);
        }
        return Ok(new { removed });
    }

    private static object ToLegacyResponse(TaggingProfile profile)
    {
        return new
        {
            name = profile.Name,
            config = BuildLegacyAutoTagConfig(profile.TagConfig, profile.AutoTag)
        };
    }

    private static Dictionary<string, JsonElement> BuildLegacyAutoTagConfig(
        UnifiedTagConfig? tagConfig,
        AutoTagSettings? autoTag)
    {
        var canonical = TaggingProfileCanonicalizer.BuildAutoTagDataFromTagConfig(
            tagConfig,
            existingData: autoTag?.Data);
        if (canonical.Count == 0)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in canonical)
        {
            payload[entry.Key] = entry.Value;
        }

        return payload;
    }

    private static AutoTagSettings ParseAutoTagSettings(JsonElement? config)
    {
        if (!config.HasValue || config.Value.ValueKind != JsonValueKind.Object)
        {
            return new AutoTagSettings();
        }

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.Value.GetRawText())
                ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            return new AutoTagSettings
            {
                Data = data
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return new AutoTagSettings();
        }
    }

    private static UnifiedTagConfig BuildTagConfigFromLegacyPayload(
        Dictionary<string, JsonElement>? autoTagData,
        UnifiedTagConfig? fallbackConfig)
    {
        if (autoTagData == null || autoTagData.Count == 0)
        {
            return fallbackConfig ?? new UnifiedTagConfig();
        }

        var hasDownloadTags = autoTagData.Keys.Any(key =>
            string.Equals(key, "downloadTags", StringComparison.OrdinalIgnoreCase));
        var hasEnrichmentTags = autoTagData.Keys.Any(key =>
            string.Equals(key, "tags", StringComparison.OrdinalIgnoreCase));

        if (!hasDownloadTags && !hasEnrichmentTags)
        {
            return fallbackConfig ?? new UnifiedTagConfig();
        }

        return TaggingProfileCanonicalizer.BuildTagConfig(fallbackConfig ?? new UnifiedTagConfig(), autoTagData);
    }

    private static AutoTagSettings SanitizeAutoTagSettings(AutoTagSettings? autoTag)
    {
        var sanitized = autoTag ?? new AutoTagSettings();
        sanitized.Data ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        _ = TaggingProfileDataHelper.StripAuthSecrets(sanitized.Data);
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

        var normalized = TaggingProfileDataHelper.NormalizeDownloadTagSource(value.GetString(), DeezerSource);
        data[key] = JsonSerializer.SerializeToElement(normalized);
    }
}
