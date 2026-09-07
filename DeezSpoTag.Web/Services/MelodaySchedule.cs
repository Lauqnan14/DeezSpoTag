using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// A named Meloday daypart slot. The name no longer implies fixed hours; each slot
/// carries a user-configured exact local generation time (HH:mm).
/// </summary>
public sealed record MelodayScheduleSlot(
    string Id,
    string Name,
    bool Enabled,
    string GenerateAt,
    int Order);

/// <summary>A single enabled slot assignment for a library: which slot and which playlist mode.</summary>
public sealed record MelodayLibrarySlotAssignment(
    string SlotId,
    string Mode)
{
    [JsonIgnore]
    public bool ProducesBothPlaylists => MelodayModes.Normalize(Mode) == MelodayModes.Both;
}

/// <summary>
/// Per-library Meloday configuration: which scheduled slots the library wants and how
/// many actual playlists it is allowed to own at once.
/// </summary>
public sealed record MelodayLibrarySchedule(
    long LibraryId,
    bool Enabled,
    int MaxActivePlaylists,
    List<MelodayLibrarySlotAssignment> Slots);

/// <summary>Canonical slot definitions, time parsing and playlist-limit arithmetic.</summary>
public static class MelodayScheduleSlots
{
    public const string EarlyMorningId = "early-morning";
    public const string MorningId = "morning";
    public const string MiddayId = "midday";
    public const string NoonId = "noon";
    public const string AfternoonId = "afternoon";
    public const string EveningId = "evening";
    public const string LateEveningId = "late-evening";

    public const int DefaultMaxActivePlaylists = 4;
    public const int MaxAllowedPlaylistsPerLibrary = 7;

    public static IReadOnlyList<MelodayScheduleSlot> Defaults { get; } = new[]
    {
        new MelodayScheduleSlot(EarlyMorningId, "Early Morning", true, "05:30", 0),
        new MelodayScheduleSlot(MorningId, "Morning", true, "08:30", 1),
        new MelodayScheduleSlot(MiddayId, "Midday", true, "11:00", 2),
        new MelodayScheduleSlot(NoonId, "Noon", true, "13:00", 3),
        new MelodayScheduleSlot(AfternoonId, "Afternoon", true, "16:00", 4),
        new MelodayScheduleSlot(EveningId, "Evening", true, "19:00", 5),
        new MelodayScheduleSlot(LateEveningId, "Late Evening", true, "22:30", 6)
    };

    private static readonly Dictionary<string, string> PhrasesById = new(StringComparer.OrdinalIgnoreCase)
    {
        [EarlyMorningId] = "in the early morning",
        [MorningId] = "in the morning",
        [MiddayId] = "at midday",
        [NoonId] = "around noon",
        [AfternoonId] = "during the afternoon",
        [EveningId] = "in the evening",
        [LateEveningId] = "late in the evening"
    };

    public static string SlotName(string? slotId)
        => Defaults.FirstOrDefault(slot => string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase))?.Name
           ?? (string.IsNullOrWhiteSpace(slotId) ? "Meloday" : slotId.Trim());

    public static string SlotPhrase(string? slotId)
        => PhrasesById.TryGetValue(slotId?.Trim().ToLowerInvariant() ?? string.Empty, out var phrase)
            ? phrase
            : "today";

    public static int SlotOrder(string? slotId)
        => Defaults.FirstOrDefault(slot => string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase))?.Order
           ?? int.MaxValue;

    public static bool IsKnownSlot(string? slotId)
        => !string.IsNullOrWhiteSpace(slotId)
           && Defaults.Any(slot => string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeSlotId(string? slotId)
    {
        var normalized = (slotId ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '-');
        return IsKnownSlot(normalized) ? normalized : string.Empty;
    }

    /// <summary>Parses "HH:mm" (24h) into minutes since local midnight; null when invalid.</summary>
    public static int? TryParseMinutes(string? generateAt)
    {
        var trimmed = (generateAt ?? string.Empty).Trim();
        var parts = trimmed.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
        {
            return null;
        }

        return (hour * 60) + minute;
    }

    public static string FormatMinutes(int minutes)
        => $"{minutes / 60:D2}:{minutes % 60:D2}";

    public static string NormalizeTime(string? generateAt, string defaultTime)
        => TryParseMinutes(generateAt) is { } minutes ? FormatMinutes(minutes) : defaultTime;

    /// <summary>Normalized canonical slot list: every known slot present exactly once, in canonical order.</summary>
    public static List<MelodayScheduleSlot> Normalize(IEnumerable<MelodayScheduleSlot>? slots)
    {
        var stored = (slots ?? Array.Empty<MelodayScheduleSlot>())
            .Where(slot => IsKnownSlot(slot.Id))
            .GroupBy(slot => NormalizeSlotId(slot.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return Defaults
            .Select(defaultSlot => stored.TryGetValue(defaultSlot.Id, out var storedSlot)
                ? new MelodayScheduleSlot(
                    defaultSlot.Id,
                    defaultSlot.Name,
                    storedSlot.Enabled,
                    NormalizeTime(storedSlot.GenerateAt, defaultSlot.GenerateAt),
                    defaultSlot.Order)
                : defaultSlot)
            .ToList();
    }

    /// <summary>Normalized per-library schedule list ordered by library id.</summary>
    public static List<MelodayLibrarySchedule> NormalizeLibraries(IEnumerable<MelodayLibrarySchedule>? libraries)
    {
        var normalized = new List<MelodayLibrarySchedule>();
        var seenLibraryIds = new HashSet<long>();
        foreach (var library in libraries ?? Array.Empty<MelodayLibrarySchedule>())
        {
            if (library.LibraryId <= 0 || !seenLibraryIds.Add(library.LibraryId))
            {
                continue;
            }

            var assignmentsBySlot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in library.Slots ?? new List<MelodayLibrarySlotAssignment>())
            {
                var slotId = NormalizeSlotId(assignment.SlotId);
                if (slotId.Length == 0)
                {
                    continue;
                }

                assignmentsBySlot[slotId] = MelodayModes.Normalize(assignment.Mode);
            }

            normalized.Add(new MelodayLibrarySchedule(
                library.LibraryId,
                library.Enabled,
                MelodayClamp.PositiveOrDefault(library.MaxActivePlaylists, DefaultMaxActivePlaylists, 1, MaxAllowedPlaylistsPerLibrary),
                assignmentsBySlot
                    .OrderBy(assignment => SlotOrder(assignment.Key))
                    .Select(assignment => new MelodayLibrarySlotAssignment(assignment.Key, assignment.Value))
                    .ToList()));
        }

        return normalized.OrderBy(library => library.LibraryId).ToList();
    }

    /// <summary>Actual playlist count for a library: "both" slots produce two playlists.</summary>
    public static int CountPlaylists(MelodayLibrarySchedule library)
        => (library.Slots ?? new List<MelodayLibrarySlotAssignment>()).Sum(assignment => assignment.ProducesBothPlaylists ? 2 : 1);

    public static string SlotIdForMix(long libraryId, string slotId, string mode)
        => $"meloday-{libraryId}-{NormalizeSlotId(slotId)}-{MelodayModes.Normalize(mode)}";
}
