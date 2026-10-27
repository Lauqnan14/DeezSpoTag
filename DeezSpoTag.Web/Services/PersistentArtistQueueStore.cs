using System.Text.Json;
using System.Threading.Channels;

namespace DeezSpoTag.Web.Services;

internal static class PersistentArtistQueueStore
{
    public static async Task<int> EnqueueArtistsAsync<TArtist, TItem>(
        IEnumerable<TArtist> artists,
        Func<TArtist, long> artistIdSelector,
        Func<TArtist, string?> artistNameSelector,
        Func<TArtist, CancellationToken, ValueTask<bool>> shouldSkipAsync,
        Func<long, string, TItem> itemFactory,
        Func<TItem, bool> tryEnqueue,
        CancellationToken cancellationToken)
    {
        var enqueued = 0;
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artistName = artistNameSelector(artist);
            if (string.IsNullOrWhiteSpace(artistName))
            {
                continue;
            }

            if (await shouldSkipAsync(artist, cancellationToken))
            {
                continue;
            }

            if (tryEnqueue(itemFactory(artistIdSelector(artist), artistName)))
            {
                enqueued++;
            }
        }

        return enqueued;
    }

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
            try
            {
                PersistQueueSnapshot(queueItems, queueLock, queuePath);
                if (channel.Writer.TryWrite(item)) return true;
                queueItems.Remove(key);
                PersistQueueSnapshot(queueItems, queueLock, queuePath);
                return false;
            }
            catch
            {
                queueItems.Remove(key);
                throw;
            }
        }
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
            var key = keySelector(item);
            var hadItem = queueItems.TryGetValue(key, out var previous);
            queueItems.Remove(key);
            try { PersistQueueSnapshot(queueItems, queueLock, queuePath); }
            catch
            {
                if (hadItem) queueItems[key] = previous!;
                throw;
            }
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

    public static bool PersistRetainedQueueSnapshot<TItem>(Dictionary<long, TItem> items,
        object queueLock, string path, ILogger logger)
    {
        try { PersistQueueSnapshot(items, queueLock, path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Retained artist work remains in memory; its queue snapshot could not be saved to {QueuePath}.", path);
            return false;
        }
    }

    public static void PersistQueueSnapshot<TItem>(
        Dictionary<long, TItem> queueItems,
        object queueLock,
        string queuePath)
    {
        lock (queueLock)
        {
            var json = JsonSerializer.Serialize(queueItems.Values.ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(queuePath)!);
            var temporary = queuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, queuePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
