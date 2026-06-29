using System.Diagnostics;

namespace DeezSpoTag.Web.Services;

public class SystemStatsService
{
    private readonly DateTimeOffset _startTime;

    public SystemStatsService()
    {
        _startTime = DateTimeOffset.UtcNow;
    }

    public string GetUptime()
    {
        var uptime = DateTimeOffset.UtcNow - _startTime;

        if (uptime.TotalSeconds < 60)
        {
            return $"{(int)uptime.TotalSeconds}s";
        }

        if (uptime.TotalMinutes < 60)
        {
            return $"{(int)uptime.TotalMinutes}m";
        }

        if (uptime.TotalHours < 24)
        {
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        }

        return $"{(int)uptime.TotalDays}d {uptime.Hours}h";
    }

    public static string GetMemoryUsage()
    {
        using var process = Process.GetCurrentProcess();
        var memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);
        return $"~{memoryMb:0} MB";
    }

    public static SystemResourceSnapshot GetResourceSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new SystemResourceSnapshot(
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            gc.HeapSizeBytes,
            gc.FragmentedBytes,
            process.Threads.Count);
    }
}

public sealed record SystemResourceSnapshot(
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long ManagedHeapBytes,
    long ManagedFragmentedBytes,
    int ProcessThreadCount);
