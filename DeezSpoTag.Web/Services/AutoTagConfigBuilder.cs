using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagConfigBuilder
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string? BuildConfigJson(TaggingProfile profile)
    {
        if (profile == null)
        {
            return null;
        }

        var canonicalData = TaggingProfileCanonicalizer.BuildAutoTagDataFromTagConfig(
            profile.TagConfig,
            profile.AutoTag?.Data);

        if (canonicalData.Count == 0)
        {
            return null;
        }

        if (profile.Technical != null)
        {
            canonicalData["technical"] = JsonSerializer.SerializeToElement(profile.Technical, _serializerOptions);
        }

        return JsonSerializer.Serialize(canonicalData, _serializerOptions);
    }
}
