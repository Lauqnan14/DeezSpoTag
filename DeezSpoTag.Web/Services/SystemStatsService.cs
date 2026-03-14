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
        var process = Process.GetCurrentProcess();
        var memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);
        return $"~{memoryMb:0} MB";
    }
}
