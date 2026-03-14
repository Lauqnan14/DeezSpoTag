using System.Collections.Concurrent;

namespace DeezSpoTag.Services.Download.Shared;

public static class DeezSpoTagSpeedTracker
{
    private sealed record SpeedSample(double BytesPerSecond, DateTimeOffset UpdatedAt);

    private static readonly ConcurrentDictionary<string, SpeedSample> Speeds = new();
    private static readonly TimeSpan SampleTtl = TimeSpan.FromSeconds(5);

    public static void ReportSpeed(string uuid, double bytesPerSecond)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return;
        }

        if (bytesPerSecond < 0)
        {
            bytesPerSecond = 0;
        }

        Speeds[uuid] = new SpeedSample(bytesPerSecond, DateTimeOffset.UtcNow);
    }

    public static double GetAggregateBytesPerSecond()
    {
        var cutoff = DateTimeOffset.UtcNow - SampleTtl;
        var total = 0.0;

        foreach (var entry in Speeds)
        {
            if (entry.Value.UpdatedAt < cutoff)
            {
                Speeds.TryRemove(entry.Key, out _);
                continue;
            }

            total += entry.Value.BytesPerSecond;
        }

        return total;
    }

    public static void Clear(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return;
        }

        Speeds.TryRemove(uuid, out _);
    }

    public static void ClearAll()
    {
        Speeds.Clear();
    }
}
