using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

internal static class TaggingProfileDataHelper
{
    private static readonly string[] LegacyFolderUniformityStructureKeys =
    {
        "usePrimaryArtistFolders",
        "multiArtistSeparator",
        "createArtistFolder",
        "artistNameTemplate",
        "createAlbumFolder",
        "albumNameTemplate",
        "createCDFolder",
        "createStructurePlaylist",
        "createSingleFolder",
        "createPlaylistFolder",
        "playlistNameTemplate",
        "illegalCharacterReplacer"
    };

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

    public static bool CanonicalizeEnhancementConfig(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("enhancement", out var enhancementElement)
            || enhancementElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var enhancementNode = JsonNode.Parse(enhancementElement.GetRawText()) as JsonObject;
            if (enhancementNode == null)
            {
                return false;
            }

            var changed = false;
            changed |= CanonicalizeFolderScopeNode(enhancementNode, "folderUniformity");
            changed |= CanonicalizeFolderScopeNode(enhancementNode, "coverMaintenance");
            changed |= CanonicalizeFolderScopeNode(enhancementNode, "qualityChecks");
            changed |= RemoveLegacyFolderUniformityStructureMirrors(enhancementNode);

            if (!changed)
            {
                return false;
            }

            data["enhancement"] = JsonSerializer.SerializeToElement(enhancementNode);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool CanonicalizeFolderScopeNode(JsonObject enhancementNode, string sectionName)
    {
        if (enhancementNode[sectionName] is not JsonObject section)
        {
            return false;
        }

        var changed = false;
        var folderIds = ReadCanonicalFolderIds(section, "folderIds");
        var legacyFolderId = TryReadLong(section["folderId"]);
        if (folderIds.Count == 0 && legacyFolderId is > 0)
        {
            folderIds.Add(legacyFolderId.Value);
            changed = true;
        }

        var normalized = new JsonArray();
        foreach (var folderId in folderIds.Distinct())
        {
            normalized.Add(folderId);
        }

        if (!HasSameFolderIds(section["folderIds"], folderIds))
        {
            section["folderIds"] = normalized;
            changed = true;
        }
        else if (section["folderIds"] is null)
        {
            section["folderIds"] = normalized;
            changed = true;
        }

        if (section.Remove("folderId"))
        {
            changed = true;
        }

        return changed;
    }

    private static bool RemoveLegacyFolderUniformityStructureMirrors(JsonObject enhancementNode)
    {
        if (enhancementNode["folderUniformity"] is not JsonObject folderUniformity)
        {
            return false;
        }

        var changed = false;
        foreach (var key in LegacyFolderUniformityStructureKeys)
        {
            changed |= folderUniformity.Remove(key);
        }

        return changed;
    }

    private static List<long> ReadCanonicalFolderIds(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj || obj[propertyName] is not JsonArray array)
        {
            return new List<long>();
        }

        return array
            .Select(TryReadLong)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
    }

    private static long? TryReadLong(JsonNode? node)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (jsonValue.TryGetValue<string>(out var stringValue)
                && long.TryParse(stringValue, out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static bool HasSameFolderIds(JsonNode? existing, IReadOnlyList<long> expected)
    {
        if (existing is not JsonArray array)
        {
            return false;
        }

        var parsed = array
            .Select(TryReadLong)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .ToList();

        if (parsed.Count != expected.Count)
        {
            return false;
        }

        for (var i = 0; i < parsed.Count; i++)
        {
            if (parsed[i] != expected[i])
            {
                return false;
            }
        }

        return true;
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
