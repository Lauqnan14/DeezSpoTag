using System.Text.Json;
using System.Threading.Channels;

namespace DeezSpoTag.Web.Services;

internal static class PersistentArtistQueueStore
{
    public static bool TryEnqueue<TItem>(
        TItem item,
        Channel<TItem> channel,
        Dictionary<long, TItem> queueItems,
        object queueLock,
        string queuePath,
        Func<TItem, long> keySelector)
    {
        lock (queueLock)
        {
            var key = keySelector(item);
            if (queueItems.ContainsKey(key))
            {
                return false;
            }

            queueItems[key] = item;
            PersistQueueSnapshot(queueItems, queueLock, queuePath);
        }

        return channel.Writer.TryWrite(item);
    }

    public static void CompleteItem<TItem>(
        TItem item,
        Dictionary<long, TItem> queueItems,
        object queueLock,
        string queuePath,
        Func<TItem, long> keySelector)
    {
        lock (queueLock)
        {
            queueItems.Remove(keySelector(item));
            PersistQueueSnapshot(queueItems, queueLock, queuePath);
        }
    }

    public static void LoadQueueSnapshot<TItem>(
        Dictionary<long, TItem> queueItems,
        object queueLock,
        string queuePath,
        Func<TItem, long> keySelector,
        Func<TItem, bool> isValid,
        ILogger logger)
    {
        lock (queueLock)
        {
            if (!File.Exists(queuePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(queuePath);
                var items = JsonSerializer.Deserialize<List<TItem>>(json) ?? new List<TItem>();
                queueItems.Clear();
                foreach (var item in items.Where(isValid))
                {
                    queueItems[keySelector(item)] = item;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to load persistent artist queue snapshot from {QueuePath}.", queuePath);
            }
        }
    }

    public static IReadOnlyList<TItem> SnapshotQueueItems<TItem>(
        Dictionary<long, TItem> queueItems,
        object queueLock)
    {
        lock (queueLock)
        {
            return queueItems.Values.ToList();
        }
    }

    public static void RestoreAndReplaySnapshot<TItem>(
        Channel<TItem> channel,
        Dictionary<long, TItem> queueItems,
        object queueLock,
        string queuePath,
        Func<TItem, long> keySelector,
        Func<TItem, bool> isValid,
        ILogger logger)
    {
        LoadQueueSnapshot(
            queueItems,
            queueLock,
            queuePath,
            keySelector,
            isValid,
            logger);

        foreach (var item in SnapshotQueueItems(queueItems, queueLock))
        {
            channel.Writer.TryWrite(item);
        }
    }

    public static void PersistQueueSnapshot<TItem>(
        Dictionary<long, TItem> queueItems,
        object queueLock,
        string queuePath)
    {
        var items = queueItems.Values.ToList();
        var json = JsonSerializer.Serialize(items);
        Directory.CreateDirectory(Path.GetDirectoryName(queuePath)!);
        File.WriteAllText(queuePath, json);
    }
}
