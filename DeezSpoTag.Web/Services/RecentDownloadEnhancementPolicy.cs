using System.Globalization;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

internal static class RecentDownloadEnhancementPolicy
{
    public const int DisabledDays = 0;
    public const int MinimumEnabledDays = 5;
    public const string DefaultLocalTimeText = "05:00";
    public static readonly TimeOnly DefaultLocalTime = new(5, 0);

    public static int NormalizeDays(int days)
    {
        if (days <= DisabledDays)
        {
            return DisabledDays;
        }

        return Math.Max(MinimumEnabledDays, days);
    }

    public static TimeOnly NormalizeTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultLocalTime;
        }

        if (TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant))
        {
            return invariant;
        }

        if (TimeOnly.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local))
        {
            return local;
        }

        return DefaultLocalTime;
    }

    public static bool IsEnabled(int days) => NormalizeDays(days) >= MinimumEnabledDays;

    public static bool IsScheduleDue(TimeOnly scheduledLocal, DateOnly? lastCheckedLocalDate, DateTimeOffset nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        if (lastCheckedLocalDate >= today)
        {
            return false;
        }

        return TimeOnly.FromDateTime(nowLocal.DateTime) >= scheduledLocal;
    }

    public static bool IsDownloadDue(DateTimeOffset completedAt, int delayDays, DateTimeOffset nowLocal)
    {
        var days = NormalizeDays(delayDays);
        if (days < MinimumEnabledDays)
        {
            return false;
        }

        var completedDate = DateOnly.FromDateTime(completedAt.ToOffset(nowLocal.Offset).DateTime);
        var cutoff = DateOnly.FromDateTime(nowLocal.DateTime).AddDays(-days);
        return completedDate <= cutoff;
    }

    public static RecentDownloadEnhancementSettings ReadSettings(TaggingProfile? profile)
    {
        var data = profile?.AutoTag?.Data;
        var days = DisabledDays;
        string? timeText = null;
        if (data != null)
        {
            if (data.TryGetValue("recentDownloadWindowDays", out var daysElement))
            {
                days = ReadInt(daysElement, DisabledDays);
            }

            if (data.TryGetValue("recentDownloadEnhancementTime", out var timeElement)
                && timeElement.ValueKind == JsonValueKind.String)
            {
                timeText = timeElement.GetString();
            }
        }

        return new RecentDownloadEnhancementSettings(NormalizeDays(days), NormalizeTime(timeText));
    }

    private static int ReadInt(JsonElement element, int fallback)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}

internal readonly record struct RecentDownloadEnhancementSettings(int Days, TimeOnly LocalTime);
