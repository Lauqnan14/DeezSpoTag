using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

/// <summary>Generation record for one playlist instance (library + slot + mode).</summary>
public sealed record MelodayRunStateEntry(
    string LastGeneratedLocalDate,
    DateTimeOffset LastGeneratedUtc,
    string Status);

/// <summary>
/// Persistent per-instance run state backing the once-per-scheduled-occurrence rule:
/// restarts, double heartbeats and repeated polls all read the same state file before
/// generating.
/// </summary>
public sealed class MelodayRunStateStore
{
    private readonly string _statePath;
    private readonly ILogger<MelodayRunStateStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public MelodayRunStateStore(IWebHostEnvironment env, ILogger<MelodayRunStateStore> logger)
    {
        _logger = logger;
        var dataDir = Path.Join(AppDataPaths.GetDataRoot(env), "meloday");
        Directory.CreateDirectory(dataDir);
        _statePath = Path.Join(dataDir, "runstate.json");
    }

    public static string Key(long libraryId, string slotId, string mode)
        => $"{libraryId}:{MelodayScheduleSlots.NormalizeSlotId(slotId)}:{MelodayModes.Normalize(mode)}";

    public async Task<MelodayRunStateEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state.TryGetValue(key, out var entry) ? entry : null;
    }

    public async Task SetAsync(string key, MelodayRunStateEntry entry, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadNoLockAsync(cancellationToken);
            state[key] = entry;
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(_statePath, json, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist Meloday run state to {Path}.", _statePath);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<Dictionary<string, MelodayRunStateEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            return await LoadNoLockAsync(cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<Dictionary<string, MelodayRunStateEntry>> LoadNoLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new Dictionary<string, MelodayRunStateEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_statePath, cancellationToken);
            var state = JsonSerializer.Deserialize<Dictionary<string, MelodayRunStateEntry>>(json, _jsonOptions);
            return state ?? new Dictionary<string, MelodayRunStateEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read Meloday run state from {Path}.", _statePath);
            return new Dictionary<string, MelodayRunStateEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Pure schedule arithmetic: daypart windows, due checks and next-slot display.</summary>
public static class MelodayScheduleMath
{
    /// <summary>
    /// The daypart window of a slot: [its generation time, the next slot's generation time),
    /// partitioning the local day by the canonical schedule. Returns the hours (0-23) whose
    /// clock-hour intersects the window.
    /// </summary>
    public static IReadOnlyList<int> DaypartHours(IReadOnlyList<MelodayScheduleSlot> slots, string slotId)
    {
        var ordered = (slots ?? Array.Empty<MelodayScheduleSlot>())
            .Select(slot => (Slot: slot, Minutes: MelodayScheduleSlots.TryParseMinutes(slot.GenerateAt)))
            .Where(entry => entry.Minutes.HasValue)
            .Select(entry => (entry.Slot, Minutes: entry.Minutes!.Value))
            .OrderBy(entry => entry.Minutes)
            .ToList();
        if (ordered.Count == 0)
        {
            return Array.Empty<int>();
        }

        var index = ordered.FindIndex(entry => string.Equals(entry.Slot.Id, MelodayScheduleSlots.NormalizeSlotId(slotId), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Array.Empty<int>();
        }

        var start = ordered[index].Minutes;
        var end = index + 1 < ordered.Count ? ordered[index + 1].Minutes : ordered[0].Minutes + 24 * 60;
        var hours = new List<int>();
        for (var minute = start; minute < end; minute++)
        {
            var hour = minute / 60 % 24;
            if (!hours.Contains(hour))
            {
                hours.Add(hour);
            }
        }

        hours.Sort();
        return hours;
    }

    /// <summary>
    /// A slot is due when its local time has passed within the grace window and it has not
    /// been generated today. Missed slots (past the grace window) are skipped for the day.
    /// </summary>
    public static bool IsDue(
        MelodayScheduleSlot slot,
        DateOnly today,
        TimeOnly nowTime,
        MelodayRunStateEntry? state,
        int graceMinutes)
    {
        var generateAt = MelodayScheduleSlots.TryParseMinutes(slot.GenerateAt);
        if (generateAt is null)
        {
            return false;
        }

        var nowMinutes = nowTime.Hour * 60 + nowTime.Minute;
        if (nowMinutes < generateAt.Value)
        {
            return false;
        }

        var grace = Math.Max(0, graceMinutes);
        if (nowMinutes > generateAt.Value + grace)
        {
            return false;
        }

        return !string.Equals(state?.LastGeneratedLocalDate, today.ToString("o"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Evening at 7:00 PM" for the next enabled slot occurrence, or null when none is enabled.</summary>
    public static string? DescribeNextSlot(IReadOnlyList<MelodayScheduleSlot> slots, DateTimeOffset now)
    {
        var nowMinutes = now.Hour * 60 + now.Minute;
        MelodayScheduleSlot? next = null;
        var nextDelta = int.MaxValue;
        foreach (var slot in slots ?? Array.Empty<MelodayScheduleSlot>())
        {
            var generateAt = MelodayScheduleSlots.TryParseMinutes(slot.GenerateAt);
            if (generateAt is null)
            {
                continue;
            }

            var delta = generateAt.Value > nowMinutes ? generateAt.Value - nowMinutes : generateAt.Value + 24 * 60 - nowMinutes;
            if (delta < nextDelta)
            {
                nextDelta = delta;
                next = slot;
            }
        }

        if (next is null)
        {
            return null;
        }

        var display = MelodayScheduleSlots.TryParseMinutes(next.GenerateAt) is { } minutes
            ? new DateTimeOffset(now.Year, now.Month, now.Day, minutes / 60, minutes % 60, 0, now.Offset)
                .ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
            : next.GenerateAt;
        return $"{next.Name} at {display}";
    }
}
