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
        if (profile.AutoTag.Data.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(profile.AutoTag.Data, _serializerOptions);
    }
}
