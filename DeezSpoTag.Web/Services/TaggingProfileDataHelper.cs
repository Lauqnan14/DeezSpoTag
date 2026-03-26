using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

internal static class TaggingProfileDataHelper
{
    public static bool StripAuthSecrets(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("custom", out var customElement) || customElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var customNode = JsonNode.Parse(customElement.GetRawText()) as JsonObject;
            if (customNode == null)
            {
                return false;
            }

            var changed = false;
            changed |= RemoveCustomField(customNode, "discogs", "token");
            changed |= RemoveCustomField(customNode, "lastfm", "apiKey");
            changed |= RemoveCustomField(customNode, "bpmsupreme", "email");
            changed |= RemoveCustomField(customNode, "bpmsupreme", "password");
            if (!changed)
            {
                return false;
            }

            data["custom"] = JsonSerializer.SerializeToElement(customNode);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public static string NormalizeDownloadTagSource(string? downloadTagSource, string defaultSource)
    {
        return downloadTagSource?.Trim().ToLowerInvariant() switch
        {
            "spotify" => "spotify",
            "deezer" => "deezer",
            _ => defaultSource
        };
    }

    private static bool RemoveCustomField(JsonObject customNode, string platformId, string field)
    {
        if (customNode[platformId] is not JsonObject platformNode)
        {
            return false;
        }

        return platformNode.Remove(field);
    }
}
