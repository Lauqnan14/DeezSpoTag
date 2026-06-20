using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public static class SpotifyContentCandidates
{
    public static IEnumerable<JsonElement> Expand(JsonElement contentData)
    {
        yield return contentData;

        if (contentData.ValueKind != JsonValueKind.Object
            || !contentData.TryGetProperty("data", out var inner)
            || inner.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return inner;
        if (inner.TryGetProperty("data", out var innerData) && innerData.ValueKind == JsonValueKind.Object)
        {
            yield return innerData;
        }
    }

    public static string? FirstString(
        IEnumerable<JsonElement> candidates,
        Func<JsonElement, string?> selector)
        => candidates.Select(selector).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public static T? FirstValue<T>(
        IEnumerable<JsonElement> candidates,
        Func<JsonElement, T?> selector)
        where T : struct
        => candidates.Select(selector).FirstOrDefault(value => value.HasValue);
}
