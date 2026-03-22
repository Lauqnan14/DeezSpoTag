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

        target.Name = normalizedName;
        target.AutoTag = ParseAutoTagSettings(profile.Config);

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
            config = BuildLegacyAutoTagConfig(profile.AutoTag)
        };
    }

    private static Dictionary<string, JsonElement> BuildLegacyAutoTagConfig(AutoTagSettings? autoTag)
    {
        if (autoTag?.Data == null || autoTag.Data.Count == 0)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in autoTag.Data)
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
}
