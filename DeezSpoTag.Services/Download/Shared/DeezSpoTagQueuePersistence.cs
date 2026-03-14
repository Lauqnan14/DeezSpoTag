using System.Text.Json;

namespace DeezSpoTag.Services.Download.Shared;

public class DeezSpoTagQueuePersistence
{
    private readonly string _queueFolder;
    private readonly string _orderFilePath;

    public DeezSpoTagQueuePersistence(string configFolder)
    {
        _queueFolder = Path.Join(configFolder, "queue");
        _orderFilePath = Path.Join(_queueFolder, "order.json");

        if (!Directory.Exists(_queueFolder))
        {
            Directory.CreateDirectory(_queueFolder);
        }
    }

    public (List<string>, Dictionary<string, Dictionary<string, object>>) RestoreQueueFromDisk()
    {
        var queueOrder = RestoreQueueOrder();
        var queue = new Dictionary<string, Dictionary<string, object>>();
        foreach (var file in EnumerateQueueItemFiles())
        {
            if (TryRestoreQueueItem(file, out var uuid, out var queueItem))
            {
                queue[uuid] = queueItem;
            }
        }

        return (queueOrder, queue);
    }

    private List<string> RestoreQueueOrder()
    {
        if (!File.Exists(_orderFilePath))
        {
            return new List<string>();
        }

        try
        {
            var orderJson = File.ReadAllText(_orderFilePath);
            return JsonSerializer.Deserialize<List<string>>(orderJson) ?? new List<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var emptyOrder = new List<string>();
            SaveQueueOrder(emptyOrder);
            return emptyOrder;
        }
    }

    private IEnumerable<string> EnumerateQueueItemFiles()
    {
        return Directory.GetFiles(_queueFolder, "*.json")
            .Where(file => !string.Equals(Path.GetFileName(file), "order.json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryRestoreQueueItem(
        string file,
        out string uuid,
        out Dictionary<string, object> queueItem)
    {
        uuid = string.Empty;
        queueItem = null!;

        if (!TryDeserializeQueueItem(file, out var item))
        {
            return false;
        }

        if (!TryReadUuid(item, out uuid))
        {
            return false;
        }

        if (!TryReadQueueType(item, out var queueType))
        {
            DeleteQueueFile(file);
            return false;
        }

        if (IsLegacyIncompatible(item, queueType))
        {
            DeleteQueueFile(file);
            return false;
        }

        EnsureQueueStatus(item);
        queueItem = item;
        return true;
    }

    private static bool TryDeserializeQueueItem(string file, out Dictionary<string, object> item)
    {
        item = null!;
        try
        {
            var json = File.ReadAllText(file);
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (deserialized is null)
            {
                return false;
            }

            item = deserialized;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteQueueFile(file);
            return false;
        }
    }

    private static bool TryReadUuid(Dictionary<string, object> item, out string uuid)
    {
        uuid = item.GetValueOrDefault("uuid")?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(uuid);
    }

    private static bool TryReadQueueType(Dictionary<string, object> item, out string queueType)
    {
        queueType = item.GetValueOrDefault("__type__")?.ToString() ?? string.Empty;
        return queueType is "Single" or "Collection";
    }

    private static bool IsLegacyIncompatible(Dictionary<string, object> item, string queueType)
    {
        return queueType == "Single"
            ? ContainsLegacyNestedField(item, "single", "trackAPI_gw")
            : ContainsLegacyNestedField(item, "collection", "tracks_gw");
    }

    private static bool ContainsLegacyNestedField(
        Dictionary<string, object> item,
        string containerField,
        string legacyField)
    {
        if (!item.TryGetValue(containerField, out var containerValue)
            || containerValue is not JsonElement element
            || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var nested = JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText());
        return nested?.ContainsKey(legacyField) == true;
    }

    private static void EnsureQueueStatus(Dictionary<string, object> item)
    {
        var status = item.GetValueOrDefault("status")?.ToString();
        if (string.IsNullOrWhiteSpace(status))
        {
            item["status"] = "inQueue";
        }
    }

    private static void DeleteQueueFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore queue cleanup failures and continue restoring remaining items.
        }
    }

    public void SaveQueueOrder(List<string> queueOrder)
    {
        var json = JsonSerializer.Serialize(queueOrder);
        File.WriteAllText(_orderFilePath, json);
    }

    public void SaveQueueItem(Dictionary<string, object> item, string status)
    {
        var uuid = item.GetValueOrDefault("uuid")?.ToString();
        if (string.IsNullOrEmpty(uuid))
        {
            throw new ArgumentException("Queue item must have a valid UUID");
        }
        
        item["status"] = status;
        var json = JsonSerializer.Serialize(item);
        File.WriteAllText(Path.Join(_queueFolder, $"{uuid}.json"), json);
    }

    public void RemoveQueueItem(string uuid)
    {
        var filePath = Path.Join(_queueFolder, $"{uuid}.json");
        if (System.IO.File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    public Dictionary<string, object>? LoadQueueItem(string uuid)
    {
        var filePath = Path.Join(_queueFolder, $"{uuid}.json");
        if (!System.IO.File.Exists(filePath))
        {
            return null;
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
    }

    /// <summary>
    /// Force clean all queue files - use this to fix ghost file issues
    /// </summary>
    public void ForceCleanAllQueueFiles()
    {
        if (Directory.Exists(_queueFolder))
        {
            // Delete all JSON files in the queue folder
            foreach (var file in Directory.GetFiles(_queueFolder, "*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) {
                    // Ignore errors when deleting files
                }
            }
        }

        // Recreate empty order.json
        SaveQueueOrder(new List<string>());
    }
}
